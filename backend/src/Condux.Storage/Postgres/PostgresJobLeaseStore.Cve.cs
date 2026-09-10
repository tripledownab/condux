using System.Text.Json;
using Condux.Core.CveFix;
using Condux.Core.FixEngine;
using Npgsql;

namespace Condux.Storage.Postgres;

/// <summary>
/// The CVE-bump side of the lease (ADR-0033 follow-up): the same claim/heartbeat/report contract over
/// cve_fix_runs, so a self-hosting org's dependency bumps run on its own compute like its issue fixes do.
/// Kept in the same class as the issue-fix SQL deliberately: one claim serves both kinds, and the
/// conditions that make a row claimable are one shared string both statements append, not two copies
/// that happen to sit in the same class.
/// </summary>
public sealed partial class PostgresJobLeaseStore
{
    // Only the FROM differs from the issue-fix claim: a bump reaches its project through the linked repo
    // rather than through an issue. The conditions and the lock are the shared
    // <see cref="ClaimableTail"/>, so they cannot drift from the statement beside them. The GHSA id
    // rides back as the job's display ref, since a bump has no issue number to name its branch after.
    private const string CveClaimSql = """
        WITH claimable AS (
            SELECT f.id, f.ghsa_id, r.project_id, r.repo_full_name
            FROM cve_fix_runs f
            JOIN repo_links r ON r.id = f.repo_link_id
            JOIN projects p ON p.id = r.project_id
        """ + ClaimableTail + """
        UPDATE cve_fix_runs SET
            status = 2,
            leased_by = @runner,
            leased_at = @now,
            lease_expires_at = @expires,
            updated_at = now()
        FROM claimable
        WHERE cve_fix_runs.id = claimable.id
        RETURNING cve_fix_runs.id, claimable.ghsa_id, claimable.project_id, claimable.repo_full_name,
                  cve_fix_runs.job_context;
        """;

    private const string CveHeartbeatSql = """
        UPDATE cve_fix_runs SET lease_expires_at = @expires, updated_at = now()
        WHERE id = @fix AND leased_by = @runner AND lease_expires_at > @now
        RETURNING id;
        """;

    private const string CveReportSql = """
        UPDATE cve_fix_runs SET
            status = @status, branch = @branch, pr_url = @pr, summary = @summary,
            model = COALESCE(NULLIF(@model, ''), model),
            input_tokens = @inputTokens, output_tokens = @outputTokens,
            lease_expires_at = NULL, updated_at = now()
        FROM repo_links r
        WHERE cve_fix_runs.id = @fix AND r.id = cve_fix_runs.repo_link_id
          AND cve_fix_runs.leased_by = @runner AND cve_fix_runs.lease_expires_at > @now
        RETURNING r.project_id;
        """;

    // Like EnqueueSql, the whole row is written here beside the claim that reads it. The lifecycle
    // columns are forced to what an enqueued job is — a pending runner-provider row with no result —
    // rather than read from the record, so a caller cannot enqueue a run in any other state.
    private const string EnqueueCveSql = """
        INSERT INTO cve_fix_runs
            (id, repo_link_id, ghsa_id, cve_id, package, ecosystem, from_range, to_version, advisory_url,
             status, provider, model, branch, pr_url, summary, actor, created_at, updated_at, job_context)
        VALUES (@id, @repo, @ghsa, @cve, @package, @ecosystem, @from, @to, @advisory,
                1, @provider, '', '', '', '', @actor, @now, @now, @context::jsonb);
        """;

    /// <summary>
    /// Queue a CVE bump for a customer-hosted runner. Only the identity, advisory and actor fields of
    /// <paramref name="run"/> are written; its lifecycle fields are ignored in favour of the pending
    /// runner-provider state every enqueued job starts in.
    /// </summary>
    public async Task EnqueueCveAsync(
        CveFixRun run, RunnerJobContext context, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(EnqueueCveSql, conn);
        cmd.Parameters.AddWithValue("id", run.Id);
        cmd.Parameters.AddWithValue("repo", run.RepoLinkId);
        cmd.Parameters.AddWithValue("ghsa", run.GhsaId);
        cmd.Parameters.AddWithValue("cve", (object?)run.CveId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("package", run.Package);
        cmd.Parameters.AddWithValue("ecosystem", run.Ecosystem);
        cmd.Parameters.AddWithValue("from", run.FromRange);
        cmd.Parameters.AddWithValue("to", run.ToVersion);
        cmd.Parameters.AddWithValue("advisory", run.AdvisoryUrl);
        cmd.Parameters.AddWithValue("provider", RunnerProvider);
        cmd.Parameters.AddWithValue("actor", run.Actor);
        cmd.Parameters.AddWithValue("now", run.CreatedAt);
        cmd.Parameters.AddWithValue("context", JsonSerializer.Serialize(context, ContextJson));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<LeasedJob?> TryClaimCveAsync(
        long orgId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(CveClaimSql, conn);
        var expires = JobLease.ExpiresAt(now);
        var leaseId = Guid.NewGuid().ToString("n");
        cmd.Parameters.AddWithValue("org", orgId);
        cmd.Parameters.AddWithValue("runner", leaseId);
        cmd.Parameters.AddWithValue("now", now);
        cmd.Parameters.AddWithValue("expires", expires);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var context = ReadContext(reader, ordinal: 4);
        // IssueId 0: a bump has no issue, and the field is pinned in the lease response for runners
        // that predate CVE routing. New runners name the work by Ref (the GHSA id) instead.
        return new LeasedJob(
            reader.GetGuid(0), IssueId: 0, reader.GetInt64(2), reader.GetString(3),
            context.BaseBranch, context.Prompt, context.ScopedPaths, expires, leaseId,
            JobKind.CveFix, reader.GetString(1));
    }
}
