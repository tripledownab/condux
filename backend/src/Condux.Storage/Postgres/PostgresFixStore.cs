using Condux.Core.Text;
using Condux.Core.FixEngine;
using Npgsql;

namespace Condux.Storage.Postgres;

/// <summary>Postgres-backed <see cref="IFixStore"/>: fix runs in <c>fix_suggestions</c> and their
/// append-only trail in <c>fix_audit</c>. This half writes them; the reads are in the Reads partial.</summary>
public sealed partial class PostgresFixStore(string connectionString) : IFixStore
{
    // A second delivery of one job meets the first one's row on job_id and inserts nothing (migration 0057).
    // ON CONFLICT names no index, so a run in progress on the issue (migration 0059) inserts nothing either,
    // rather than raising before the orchestrator can tell. The job's reservation is not handed back here: a
    // job whose own run exists had it settled with that run, and for a job published before requests wrote
    // their run nothing records the refusal, so a second delivery would refund it again.
    private const string InsertSql = """
        INSERT INTO fix_suggestions
            (id, issue_id, repo_full_name, status, provider, model, branch, pr_url, summary, created_at, updated_at,
             job_id, reserved_at)
        VALUES (@id, @issue, @repo, @status, @provider, @model, @branch, @pr, @summary, @created, @updated,
                @job, @reserved)
        ON CONFLICT DO NOTHING;
        """;

    // What concluding a run needs, under its row lock (RunConclusions): the month it was reserved in, and
    // the tier whose limit bounds taking it back.
    private const string LockSql = """
        SELECT fs.status, fs.failure_reason, p.org_id, o.tier, COALESCE(fs.reserved_at, fs.created_at)
        FROM fix_suggestions fs
        JOIN issues i ON i.id = fs.issue_id
        JOIN projects p ON p.id = i.project_id
        JOIN orgs o ON o.id = p.org_id
        WHERE fs.id = @id
        FOR UPDATE OF fs;
        """;

    private const string UpdateSql = $"""
        UPDATE fix_suggestions
          {SetOutcome}
        WHERE id = @id;
        """;

    // A failure lands only on a run still in progress (1 pending, 2 running), so a late one cannot undo a
    // conclusion the stale-run sweep already wrote and refunded.
    // Only over a pending run, so a run the stale-run sweep already concluded stays concluded.
    private const string MarkRunningSql = $"""
        UPDATE fix_suggestions
          {SetOutcome}
        WHERE id = @id AND status = 1;
        """;

    private const string TryFailSql = $"""
        UPDATE fix_suggestions
          {SetOutcome}
        WHERE id = @id AND status IN (1, 2);
        """;

    private const string SetOutcome = """
        SET status = @status, branch = @branch, pr_url = @pr, summary = @summary,
              input_tokens = @in, output_tokens = @out, failure_reason = @reason, updated_at = @updated
        """;

    private const string AuditSql = """
        INSERT INTO fix_audit (fix_id, actor, event, detail) VALUES (@fix, @actor, @event, @detail::jsonb);
        """;

    public async Task<bool> InsertAsync(FixSuggestion s, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(InsertSql, conn);
        cmd.Parameters.AddWithValue("id", s.Id);
        cmd.Parameters.AddWithValue("issue", s.IssueId);
        cmd.Parameters.AddWithValue("repo", s.RepoFullName);
        cmd.Parameters.AddWithValue("status", (short)s.Status);
        cmd.Parameters.AddWithValue("provider", s.Provider);
        cmd.Parameters.AddWithValue("model", s.Model);
        cmd.Parameters.AddWithValue("branch", s.Branch);
        cmd.Parameters.AddWithValue("pr", s.PrUrl);
        cmd.Parameters.AddWithValue("summary", StorableText.ToStorable(s.Summary));
        cmd.Parameters.AddWithValue("created", s.CreatedAt);
        cmd.Parameters.AddWithValue("updated", s.UpdatedAt);
        cmd.Parameters.AddWithValue("job", (object?)s.JobId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("reserved", (object?)s.ReservedAt ?? DBNull.Value);
        return await cmd.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<bool> MarkRunningAsync(FixSuggestion s, CancellationToken cancellationToken = default) =>
        await WriteAsync(MarkRunningSql, s, cancellationToken) == 1;

    /// <summary>Writes the run's outcome fields whatever its state. Nothing in production calls it; tests use
    /// it to put a run in the state they start from.</summary>
    public async Task UpdateAsync(FixSuggestion s, CancellationToken cancellationToken = default) =>
        await WriteAsync(UpdateSql, s, cancellationToken);

    private async Task<int> WriteAsync(string sql, FixSuggestion s, CancellationToken cancellationToken)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        return await WriteOutcomeAsync(conn, null, sql, s, cancellationToken);
    }

    public Task<bool> TryFailAsync(FixSuggestion s, CancellationToken cancellationToken = default) =>
        RunConclusions.TryFailAsync(
            connectionString, LockSql, s.Id,
            (conn, tx) => WriteOutcomeAsync(conn, tx, TryFailSql, s, cancellationToken), cancellationToken);

    public Task RecordSuccessAsync(FixSuggestion s, CancellationToken cancellationToken = default) =>
        RunConclusions.RecordSuccessAsync(
            connectionString, LockSql, s.Id,
            (conn, tx) => WriteOutcomeAsync(conn, tx, UpdateSql, s, cancellationToken), cancellationToken);

    private static async Task<int> WriteOutcomeAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, string sql, FixSuggestion s, CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("id", s.Id);
        cmd.Parameters.AddWithValue("status", (short)s.Status);
        cmd.Parameters.AddWithValue("branch", s.Branch);
        cmd.Parameters.AddWithValue("pr", s.PrUrl);
        cmd.Parameters.AddWithValue("summary", StorableText.ToStorable(s.Summary));
        cmd.Parameters.AddWithValue("in", s.InputTokens);
        cmd.Parameters.AddWithValue("out", s.OutputTokens);
        cmd.Parameters.AddWithValue("reason", FixFailureColumn.ToParameter(s.FailureReason));
        cmd.Parameters.AddWithValue("updated", s.UpdatedAt);
        return await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AppendAuditAsync(
        Guid fixId, string actor, string eventName, string detailJson, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(AuditSql, conn);
        cmd.Parameters.AddWithValue("fix", fixId);
        cmd.Parameters.AddWithValue("actor", actor);
        cmd.Parameters.AddWithValue("event", eventName);
        cmd.AddJsonb("detail", detailJson);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
