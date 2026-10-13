using Condux.Core.CveFix;
using Condux.Core.FixEngine;
using Npgsql;
using NpgsqlTypes;

namespace Condux.Storage.Postgres;

/// <summary>Postgres-backed <see cref="ICveFixStore"/>: CVE-bump runs in <c>cve_fix_runs</c>. This half
/// writes them; the reads are in the Reads partial.</summary>
public sealed partial class PostgresCveFixStore(string connectionString) : ICveFixStore
{
    // ON CONFLICT names no index, so neither unique index can raise: a job delivered again meets its own
    // run on job_id, and a job whose advisory has a run in progress meets that run on the in-progress index.
    // A job refused once is never written later either, or it would run on the reservation it handed back.
    private const string InsertSql = """
        INSERT INTO cve_fix_runs
            (id, repo_link_id, ghsa_id, cve_id, package, ecosystem, from_range, to_version, advisory_url,
             status, provider, model, branch, pr_url, summary, actor, created_at, updated_at, job_id, reserved_at,
             vulnerable_ranges)
        SELECT @id, @repo, @ghsa, @cve, @package, @ecosystem, @from, @to, @advisory,
               @status, @provider, @model, @branch, @pr, @summary, @actor, @created, @updated, @job, @reserved,
               @ranges
        WHERE NOT EXISTS (SELECT 1 FROM cve_fix_skipped_jobs WHERE job_id = @job)
        ON CONFLICT DO NOTHING;
        """;

    // Whether the job has a run already, which makes this a delivery of it again.
    private const string SameJobSql = "SELECT EXISTS (SELECT 1 FROM cve_fix_runs WHERE job_id = @job);";

    // Records a refused job. Only the delivery whose insert writes the row pays its refund.
    private const string SkipSql = "INSERT INTO cve_fix_skipped_jobs (job_id) VALUES (@job) ON CONFLICT DO NOTHING;";

    /// <summary>A run's vulnerable ranges as the <c>vulnerable_ranges</c> column holds them: null when the
    /// run carries none, which is what a verifier reads as a run requested before they were stored.</summary>
    internal static object ToRangesParameter(IReadOnlyList<string>? ranges) =>
        (object?)ranges?.ToArray() ?? DBNull.Value;

    // The org whose allowance a run on this repository was reserved from.
    private const string OrgOfRepoSql = """
        SELECT p.org_id FROM repo_links l JOIN projects p ON p.id = l.project_id WHERE l.id = @repo;
        """;

    // What concluding a run needs, under its row lock (RunConclusions), through the linked repository.
    private const string LockSql = """
        SELECT r.status, r.failure_reason, p.org_id, o.tier, COALESCE(r.reserved_at, r.created_at)
        FROM cve_fix_runs r
        JOIN repo_links l ON l.id = r.repo_link_id
        JOIN projects p ON p.id = l.project_id
        JOIN orgs o ON o.id = p.org_id
        WHERE r.id = @id
        FOR UPDATE OF r;
        """;

    private const string UpdateSql = $"""
        UPDATE cve_fix_runs
          {SetOutcome}
        WHERE id = @id;
        """;

    // A failure lands only on a run still in progress (1 pending, 2 running), so a late one cannot undo a
    // conclusion the stale-run sweep already wrote and refunded.
    // Only over a pending run, so a run the stale-run sweep already concluded stays concluded.
    private const string MarkRunningSql = $"""
        UPDATE cve_fix_runs
          {SetOutcome}
        WHERE id = @id AND status = 1;
        """;

    private const string TryFailSql = $"""
        UPDATE cve_fix_runs
          {SetOutcome}
        WHERE id = @id AND status IN (1, 2);
        """;

    private const string SetOutcome = """
        SET status = @status, branch = @branch, pr_url = @pr, summary = @summary,
              input_tokens = @in, output_tokens = @out, failure_reason = @reason, updated_at = @updated
        """;

    public async Task<bool> InsertAsync(CveFixRun r, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(InsertSql, conn, tx);
        cmd.Parameters.AddWithValue("id", r.Id);
        cmd.Parameters.AddWithValue("repo", r.RepoLinkId);
        cmd.Parameters.AddWithValue("ghsa", r.GhsaId);
        cmd.Parameters.AddWithValue("cve", (object?)r.CveId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("package", r.Package);
        cmd.Parameters.AddWithValue("ecosystem", r.Ecosystem);
        cmd.Parameters.AddWithValue("from", r.FromRange);
        cmd.Parameters.AddWithValue("to", r.ToVersion);
        cmd.Parameters.AddWithValue("advisory", r.AdvisoryUrl);
        cmd.Parameters.AddWithValue("status", (short)r.Status);
        cmd.Parameters.AddWithValue("provider", r.Provider);
        cmd.Parameters.AddWithValue("model", r.Model);
        cmd.Parameters.AddWithValue("branch", r.Branch);
        cmd.Parameters.AddWithValue("pr", r.PrUrl);
        cmd.Parameters.AddWithValue("summary", r.Summary);
        cmd.Parameters.AddWithValue("actor", r.Actor);
        cmd.Parameters.AddWithValue("created", r.CreatedAt);
        cmd.Parameters.AddWithValue("updated", r.UpdatedAt);
        cmd.Parameters.AddWithValue("ranges", ToRangesParameter(r.VulnerableRanges));
        // Typed: the job id is compared in the WHERE clause, which has no destination column to take a type from.
        cmd.Parameters.Add(new NpgsqlParameter("job", NpgsqlDbType.Uuid) { Value = (object?)r.JobId ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("reserved", NpgsqlDbType.TimestampTz)
        {
            Value = (object?)r.ReservedAt ?? DBNull.Value,
        });
        if (await cmd.ExecuteNonQueryAsync(cancellationToken) == 1)
        {
            await tx.CommitAsync(cancellationToken);
            return true;
        }

        // Nothing was written: the job has a run already, was refused before, or another run is in progress
        // on its advisory. Only the last hands its reservation back, and only once per job, however often it
        // is delivered. A job with no id
        // cannot be told from a delivery again, so it is not refunded. The refund goes to the monthly
        // allowance, like every refund of a run the conductor took.
        if (r.JobId is { } jobId
            && !await SameJobExistsAsync(conn, tx, jobId, cancellationToken)
            && await RecordSkipAsync(conn, tx, jobId, cancellationToken))
        {
            await PostgresAiFixQuota.RefundAsync(
                conn, tx, await OrgOfRepoAsync(conn, tx, r.RepoLinkId, cancellationToken),
                r.ReservedAt ?? r.CreatedAt, cancellationToken);
        }
        await tx.CommitAsync(cancellationToken);
        return false;
    }

    private static async Task<bool> RecordSkipAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid jobId, CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand(SkipSql, conn, tx);
        cmd.Parameters.AddWithValue("job", jobId);
        return await cmd.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static async Task<bool> SameJobExistsAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid jobId, CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand(SameJobSql, conn, tx);
        cmd.Parameters.AddWithValue("job", jobId);
        return (bool)(await cmd.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task<long> OrgOfRepoAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid repoLinkId, CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand(OrgOfRepoSql, conn, tx);
        cmd.Parameters.AddWithValue("repo", repoLinkId);
        return (long)(await cmd.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task<bool> MarkRunningAsync(CveFixRun r, CancellationToken cancellationToken = default) =>
        await WriteAsync(MarkRunningSql, r, cancellationToken) == 1;

    /// <summary>Writes the run's outcome fields whatever its state. Nothing in production calls it; tests use
    /// it to put a run in the state they start from.</summary>
    public async Task UpdateAsync(CveFixRun r, CancellationToken cancellationToken = default) =>
        await WriteAsync(UpdateSql, r, cancellationToken);

    private async Task<int> WriteAsync(string sql, CveFixRun r, CancellationToken cancellationToken)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        return await WriteOutcomeAsync(conn, null, sql, r, cancellationToken);
    }

    public Task<bool> TryFailAsync(CveFixRun r, CancellationToken cancellationToken = default) =>
        RunConclusions.TryFailAsync(
            connectionString, LockSql, r.Id,
            (conn, tx) => WriteOutcomeAsync(conn, tx, TryFailSql, r, cancellationToken), cancellationToken);

    public Task RecordSuccessAsync(CveFixRun r, CancellationToken cancellationToken = default) =>
        RunConclusions.RecordSuccessAsync(
            connectionString, LockSql, r.Id,
            (conn, tx) => WriteOutcomeAsync(conn, tx, UpdateSql, r, cancellationToken), cancellationToken);

    private static async Task<int> WriteOutcomeAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, string sql, CveFixRun r, CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("id", r.Id);
        cmd.Parameters.AddWithValue("status", (short)r.Status);
        cmd.Parameters.AddWithValue("branch", r.Branch);
        cmd.Parameters.AddWithValue("pr", r.PrUrl);
        cmd.Parameters.AddWithValue("summary", r.Summary);
        cmd.Parameters.AddWithValue("in", r.InputTokens);
        cmd.Parameters.AddWithValue("out", r.OutputTokens);
        cmd.Parameters.AddWithValue("reason", FixFailureColumn.ToParameter(r.FailureReason));
        cmd.Parameters.AddWithValue("updated", r.UpdatedAt);
        return await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
