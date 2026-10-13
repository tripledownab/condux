using System.Globalization;
using System.Text.Json;
using Condux.Core.FixEngine;
using Npgsql;

namespace Condux.Storage.Postgres;

/// <summary>A job handed to a runner, with everything it needs to execute without asking us again.
/// <c>ProjectId</c> is for the caller's own side effects (the live-badge nudge), not for the runner.
/// <c>Kind</c> is server-side only — the runner executes both shapes identically, but the server audits
/// and reports them differently. <c>Ref</c> is the work's display identifier (the issue id, or the GHSA
/// id of a CVE bump) for branch names and pull-request copy.</summary>
public sealed record LeasedJob(
    Guid FixId, long IssueId, long ProjectId, string RepoFullName, string BaseBranch, string Prompt,
    IReadOnlyList<string> ScopedPaths, DateTimeOffset LeaseExpiresAt, string LeaseId,
    JobKind Kind, string Ref);

/// <summary>A report that landed: which project to nudge, and which kind of run it concluded (an issue
/// fix gets a fix_audit entry; a CVE bump has no audit table and its row is the whole record).</summary>
public sealed record ReportedJob(long ProjectId, JobKind Kind);

/// <summary>
/// Where a customer-hosted runner takes work from and reports it back (ADR-0033 slice 4). Serves both
/// run kinds — issue fixes (fix_suggestions) and CVE bumps (cve_fix_runs, in the Cve partial) — behind
/// one claim, so a runner polls one queue and both statements append the same claim conditions
/// (<see cref="ClaimableTail"/>) rather than restating them per kind.
///
/// Claiming is one guarded UPDATE with RETURNING, for the same reason the allowance reservation is: two
/// runners polling at once would otherwise both read a job as free and both take it, and one fix would
/// open two pull requests. The row lock decides, and no row returned means somebody else won the race.
///
/// The claim conditions are here rather than in a predicate anywhere else, because a second statement of
/// them is free to drift from the one that actually runs.
/// </summary>
public sealed partial class PostgresJobLeaseStore(string connectionString)
{
    // Claimable: work routed to a runner (it carries a job context; a hosted run does not) that is pending
    // or whose lease has lapsed, oldest first so nothing starves.
    //
    // The job_context condition is load bearing, not a convenience. A run the hosted Conductor is executing
    // is Running with no lease for its whole duration, so without it every in-flight hosted fix reads as
    // free and a runner would open a second draft pull request for the same issue.
    //
    // FOR UPDATE SKIP LOCKED lets concurrent runners pass over a row another is claiming rather than
    // queue behind it, which is what keeps a busy fleet from serialising on one lock.
    //
    // Everything from the org filter down is shared verbatim with the CVE claim, which is why both alias
    // their run table `f`: one string appended to two queries, not two copies to compare by eye. The
    // blank lines around it are what keep the joined text readable, since a raw literal drops the
    // newline beside its delimiters. Postgres parses it either way, measured; a person does not.
    private const string ClaimableTail = """

            WHERE p.org_id = @org
              AND f.job_context IS NOT NULL
              AND f.status IN (1, 2)
              AND
        """ + LeaseIsFree + """

            ORDER BY f.created_at
            FOR UPDATE OF f SKIP LOCKED
            LIMIT 1
        )

        """;

    private const string ClaimSql = """
        WITH claimable AS (
            SELECT f.id, i.project_id
            FROM fix_suggestions f
            JOIN issues i ON i.id = f.issue_id
            JOIN projects p ON p.id = i.project_id
        """ + ClaimableTail + """
        UPDATE fix_suggestions SET
            status = 2,
            leased_by = @runner,
            leased_at = @now,
            lease_expires_at = @expires,
            updated_at = now()
        FROM claimable
        WHERE fix_suggestions.id = claimable.id
        RETURNING fix_suggestions.id, fix_suggestions.issue_id, claimable.project_id,
                  fix_suggestions.repo_full_name, fix_suggestions.job_context;
        """;

    // Extending requires still holding it: a runner whose lease lapsed has probably had the work taken,
    // and letting it heartbeat back into ownership would give two runners one job.
    private const string HeartbeatSql = """
        UPDATE fix_suggestions SET lease_expires_at = @expires, updated_at = now()
        WHERE id = @fix AND leased_by = @runner AND lease_expires_at > @now
        RETURNING id;
        """;

    // Creating the row and claiming it are the same contract seen from two sides, so they live together:
    // job_context is what a claim looks for, and this is the only thing that sets it.
    private const string EnqueueSql = """
        INSERT INTO fix_suggestions
            (id, issue_id, repo_full_name, status, provider, model, branch, pr_url, summary,
             created_at, updated_at, reserved_at, job_context)
        VALUES (@id, @issue, @repo, 1, @provider, '', '', '', '', @now, @now, @now, @context::jsonb)
        ON CONFLICT DO NOTHING;
        """;

    /// <summary>The provider name a runner-executed fix carries. The model is the customer's and unknown
    /// until they report it, but where the run happened is known at enqueue and is what an operator reading
    /// the run actually wants to see.</summary>
    public const string RunnerProvider = "runner";

    /// <summary>
    /// Queue a fix for a customer-hosted runner to take. This is where RequestFix and the auto-fix
    /// dispatcher send a run when the org's execution setting says its own runner does the work
    /// (ADR-0033 slice 4c) — the same request path either way, ending in a row here instead of a Kafka
    /// message. False when nothing was queued because the issue already has a run in progress.
    /// </summary>
    public async Task<bool> EnqueueAsync(
        Guid fixId, long issueId, string repoFullName, RunnerJobContext context, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(EnqueueSql, conn);
        cmd.Parameters.AddWithValue("id", fixId);
        cmd.Parameters.AddWithValue("issue", issueId);
        cmd.Parameters.AddWithValue("repo", repoFullName);
        cmd.Parameters.AddWithValue("provider", RunnerProvider);
        cmd.Parameters.AddWithValue("now", now);
        cmd.Parameters.AddWithValue("context", JsonSerializer.Serialize(context, ContextJson));
        return await cmd.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    /// <summary>
    /// Take the next job for an org, or null when there is none. Null is the ordinary answer: a runner
    /// polls continuously and most polls find nothing. Issue fixes are offered before CVE bumps — an
    /// issue has a person or an alert behind it, a bump can wait one more poll.
    /// </summary>
    public async Task<LeasedJob?> TryClaimAsync(
        long orgId, DateTimeOffset now, CancellationToken cancellationToken = default)
        => await TryClaimFixAsync(orgId, now, cancellationToken)
            ?? await TryClaimCveAsync(orgId, now, cancellationToken);

    private async Task<LeasedJob?> TryClaimFixAsync(
        long orgId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(ClaimSql, conn);
        var expires = JobLease.ExpiresAt(now);
        // Unguessable, and stored as the holder, so only the runner handed this value can act on the job.
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

        // The claim has committed, so a context that will not deserialize cannot un-claim the job. It is
        // handed over empty and the runner reports it failed, which is the right outcome: a job whose
        // context we cannot read is not a job to guess at, and failing it refunds the allowance.
        var context = ReadContext(reader, ordinal: 4);
        var issueId = reader.GetInt64(1);
        return new LeasedJob(
            reader.GetGuid(0), issueId, reader.GetInt64(2), reader.GetString(3),
            context.BaseBranch, context.Prompt, context.ScopedPaths, expires, leaseId,
            JobKind.IssueFix, issueId.ToString(CultureInfo.InvariantCulture));
    }

    // Written by the request path as camelCase JSON, so the column reads the same from psql as from here.
    private static readonly JsonSerializerOptions ContextJson =
        new(JsonSerializerDefaults.Web);

    private static RunnerJobContext ReadContext(NpgsqlDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return RunnerJobContext.None;
        }

        try
        {
            return JsonSerializer.Deserialize<RunnerJobContext>(reader.GetString(ordinal), ContextJson)
                ?? RunnerJobContext.None;
        }
        catch (JsonException)
        {
            return RunnerJobContext.None;
        }
    }

    /// <summary>Extend a held lease, proved by the lease id. False means it was lost; stop working.
    /// Tried against both run tables — the id plus the unguessable lease id can only match the row the
    /// caller actually holds, so the fallback cannot touch anyone else's work.</summary>
    public async Task<bool> TryHeartbeatAsync(
        Guid fixId, string leaseId, DateTimeOffset now, CancellationToken cancellationToken = default)
        => await TouchAsync(HeartbeatSql, fixId, leaseId, now, cancellationToken)
            || await TouchAsync(CveHeartbeatSql, fixId, leaseId, now, cancellationToken);

    private async Task<bool> TouchAsync(
        string sql, Guid fixId, string leaseId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("fix", fixId);
        cmd.Parameters.AddWithValue("runner", leaseId);
        cmd.Parameters.AddWithValue("now", now);
        cmd.Parameters.AddWithValue("expires", JobLease.ExpiresAt(now));
        return await cmd.ExecuteScalarAsync(cancellationToken) is not null;
    }
}
