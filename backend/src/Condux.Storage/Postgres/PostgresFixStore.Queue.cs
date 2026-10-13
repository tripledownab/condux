using Condux.Core.FixEngine;
using Npgsql;

namespace Condux.Storage.Postgres;

/// <summary>The request's half of a fix run's lifecycle: the pending run written before the job is
/// published, cancelled if queueing it failed or withdrawn by an admin while nothing holds it, and
/// claimed by the conductor when the job arrives.</summary>
public sealed partial class PostgresFixStore
{
    // ON CONFLICT names no index, so neither unique index raises: another run in progress on the issue
    // (fix_suggestions_active_uq) is the answer this exists to give. The provider is left empty until the
    // conductor claims the run, since which provider runs it is the conductor's setting.
    private const string QueueSql = """
        INSERT INTO fix_suggestions
            (id, issue_id, repo_full_name, status, provider, model, branch, pr_url, summary, created_at, updated_at,
             job_id, reserved_at)
        VALUES (@id, @issue, @repo, 1, '', @model, '', '', '', @created, @created, @job, @reserved)
        ON CONFLICT DO NOTHING;
        """;

    // Cancelled (5), not deleted: the row keeps its job id, which is what refuses the job if it arrives.
    // An insert on the run's own id rather than an update, because the write being settled may still be in
    // flight after its client gave up, and an update cannot see an uncommitted row. An insert on that key
    // waits for it: committed and pending, it is cancelled; committed and already taken, the statement writes
    // no row; never written, the run is written already cancelled, and a late write then meets it and writes
    // nothing, since the queue and runner inserts name no conflict target. The row count is the answer.
    private const string CancelQueuedSql = """
        INSERT INTO fix_suggestions
            (id, issue_id, repo_full_name, status, provider, model, branch, pr_url, summary, created_at, updated_at,
             job_id, reserved_at)
        VALUES (@id, @issue, @repo, 5, '', @model, '', '', '', @created, @created, @job, @reserved)
        ON CONFLICT (id) DO UPDATE SET status = 5, updated_at = now() WHERE fix_suggestions.status = 1;
        """;

    // Withdraws a run nothing holds; PostgresJobLeaseStore.WithdrawTail says which runs those are.
    private const string WithdrawSql = "UPDATE fix_suggestions" + PostgresJobLeaseStore.WithdrawTail;

    // Only a pending hosted run: one the sweep failed is not run, since its allowance went back with it.
    private const string ClaimSql = """
        UPDATE fix_suggestions
        SET status = 2, provider = @provider, model = @model, updated_at = @updated
        WHERE job_id = @job AND status = 1 AND job_context IS NULL
        RETURNING id;
        """;

    public async Task<bool> TryQueueAsync(FixSuggestion s, CancellationToken cancellationToken = default) =>
        await WriteRunAsync(QueueSql, s, cancellationToken) == 1;

    public async Task<bool> TryCancelQueuedAsync(FixSuggestion s, CancellationToken cancellationToken = default) =>
        await WriteRunAsync(CancelQueuedSql, s, cancellationToken) == 1;

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

    private async Task<int> WriteRunAsync(string sql, FixSuggestion s, CancellationToken cancellationToken)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", s.Id);
        cmd.Parameters.AddWithValue("issue", s.IssueId);
        cmd.Parameters.AddWithValue("repo", s.RepoFullName);
        cmd.Parameters.AddWithValue("model", s.Model);
        cmd.Parameters.AddWithValue("created", s.CreatedAt);
        cmd.Parameters.AddWithValue("job", (object?)s.JobId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("reserved", (object?)s.ReservedAt ?? DBNull.Value);
        return await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<Guid?> TryClaimAsync(FixSuggestion s, CancellationToken cancellationToken = default)
    {
        if (s.JobId is not { } jobId)
        {
            return null;
        }

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(ClaimSql, conn);
        cmd.Parameters.AddWithValue("job", jobId);
        cmd.Parameters.AddWithValue("provider", s.Provider);
        cmd.Parameters.AddWithValue("model", s.Model);
        cmd.Parameters.AddWithValue("updated", s.UpdatedAt);
        return await cmd.ExecuteScalarAsync(cancellationToken) is Guid id ? id : null;
    }
}
