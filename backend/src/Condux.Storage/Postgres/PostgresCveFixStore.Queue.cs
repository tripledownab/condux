using Condux.Core.CveFix;
using Npgsql;

namespace Condux.Storage.Postgres;

/// <summary>The request's half of a CVE bump's lifecycle: the pending run written before the job is
/// published, cancelled if queueing it failed or withdrawn by an admin while nothing holds it, and
/// claimed by the conductor when the job arrives.</summary>
public sealed partial class PostgresCveFixStore
{
    // ON CONFLICT names no index, so neither unique index raises: another run in progress on the advisory
    // (cve_fix_runs_active_uq) is the answer this exists to give. The provider is left empty until the
    // conductor claims the run, since which provider runs it is the conductor's setting.
    private const string QueueSql = """
        INSERT INTO cve_fix_runs
            (id, repo_link_id, ghsa_id, cve_id, package, ecosystem, from_range, to_version, advisory_url,
             status, provider, model, branch, pr_url, summary, actor, created_at, updated_at, job_id, reserved_at,
             vulnerable_ranges)
        VALUES (@id, @repo, @ghsa, @cve, @package, @ecosystem, @from, @to, @advisory,
                1, '', @model, '', '', '', @actor, @created, @created, @job, @reserved, @ranges)
        ON CONFLICT DO NOTHING;
        """;

    // Cancelled (5), not deleted: the row keeps its job id, which is what refuses the job if it arrives.
    // An insert on the run's own id rather than an update, because the write being settled may still be in
    // flight after its client gave up, and an update cannot see an uncommitted row. An insert on that key
    // waits for it: committed and pending, it is cancelled; committed and already taken, the statement writes
    // no row; never written, the run is written already cancelled, and a late write then meets it and writes
    // nothing, since the queue and runner inserts name no conflict target. The row count is the answer.
    private const string CancelQueuedSql = """
        INSERT INTO cve_fix_runs
            (id, repo_link_id, ghsa_id, cve_id, package, ecosystem, from_range, to_version, advisory_url,
             status, provider, model, branch, pr_url, summary, actor, created_at, updated_at, job_id, reserved_at,
             vulnerable_ranges)
        VALUES (@id, @repo, @ghsa, @cve, @package, @ecosystem, @from, @to, @advisory,
                5, '', @model, '', '', '', @actor, @created, @created, @job, @reserved, @ranges)
        ON CONFLICT (id) DO UPDATE SET status = 5, updated_at = now() WHERE cve_fix_runs.status = 1;
        """;

    // Withdraws a run nothing holds; PostgresJobLeaseStore.WithdrawTail says which runs those are.
    private const string WithdrawSql = "UPDATE cve_fix_runs" + PostgresJobLeaseStore.WithdrawTail;

    // Only a pending hosted run: one the sweep failed is not run, since its allowance went back with it.
    private const string ClaimSql = """
        UPDATE cve_fix_runs
        SET status = 2, provider = @provider, model = @model, updated_at = @updated
        WHERE job_id = @job AND status = 1 AND job_context IS NULL
        RETURNING id;
        """;

    public async Task<bool> TryQueueAsync(CveFixRun r, CancellationToken cancellationToken = default) =>
        await WriteRunAsync(QueueSql, r, cancellationToken) == 1;

    public async Task<bool> TryCancelQueuedAsync(CveFixRun r, CancellationToken cancellationToken = default) =>
        await WriteRunAsync(CancelQueuedSql, r, cancellationToken) == 1;

    /// <summary>Withdraws a run nothing holds and hands its reservation back, in one transaction, so a
    /// run queued for a runner that never comes, or whose runner lost its lease, no longer holds its issue or
    /// advisory. <paramref name="now"/> is the control-plane clock the lease claim and report also read. False
    /// when something holds the run, or it is concluded or gone.</summary>
    public Task<bool> TryWithdrawAsync(
        Guid runId, DateTimeOffset now, CancellationToken cancellationToken = default) =>
        RunConclusions.TryWithdrawAsync(
            connectionString, LockSql, runId,
            async (conn, tx) =>
            {
                await using var cmd = new NpgsqlCommand(WithdrawSql, conn, tx);
                cmd.Parameters.AddWithValue("id", runId);
                cmd.Parameters.AddWithValue("now", now);
                return await cmd.ExecuteNonQueryAsync(cancellationToken);
            },
            cancellationToken);

    private async Task<int> WriteRunAsync(string sql, CveFixRun r, CancellationToken cancellationToken)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", r.Id);
        cmd.Parameters.AddWithValue("repo", r.RepoLinkId);
        cmd.Parameters.AddWithValue("ghsa", r.GhsaId);
        cmd.Parameters.AddWithValue("cve", (object?)r.CveId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("package", r.Package);
        cmd.Parameters.AddWithValue("ecosystem", r.Ecosystem);
        cmd.Parameters.AddWithValue("from", r.FromRange);
        cmd.Parameters.AddWithValue("to", r.ToVersion);
        cmd.Parameters.AddWithValue("advisory", r.AdvisoryUrl);
        cmd.Parameters.AddWithValue("model", r.Model);
        cmd.Parameters.AddWithValue("actor", r.Actor);
        cmd.Parameters.AddWithValue("created", r.CreatedAt);
        cmd.Parameters.AddWithValue("job", (object?)r.JobId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("reserved", (object?)r.ReservedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("ranges", ToRangesParameter(r.VulnerableRanges));
        return await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<Guid?> TryClaimAsync(CveFixRun r, CancellationToken cancellationToken = default)
    {
        if (r.JobId is not { } jobId)
        {
            return null;
        }

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(ClaimSql, conn);
        cmd.Parameters.AddWithValue("job", jobId);
        cmd.Parameters.AddWithValue("provider", r.Provider);
        cmd.Parameters.AddWithValue("model", r.Model);
        cmd.Parameters.AddWithValue("updated", r.UpdatedAt);
        return await cmd.ExecuteScalarAsync(cancellationToken) is Guid id ? id : null;
    }
}
