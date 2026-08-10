using Condux.Core.FixEngine;
using Condux.Core.WeeklySummaries;
using Npgsql;

namespace Condux.Storage.Postgres;

/// <summary>Postgres-backed <see cref="IFixStore"/>: fix runs in <c>fix_suggestions</c> and their
/// append-only trail in <c>fix_audit</c>.</summary>
public sealed class PostgresFixStore(string connectionString) : IFixStore
{
    private const string InsertSql = """
        INSERT INTO fix_suggestions
            (id, issue_id, repo_full_name, status, provider, model, branch, pr_url, summary, created_at, updated_at)
        VALUES (@id, @issue, @repo, @status, @provider, @model, @branch, @pr, @summary, @created, @updated);
        """;

    private const string UpdateSql = """
        UPDATE fix_suggestions
          SET status = @status, branch = @branch, pr_url = @pr, summary = @summary,
              input_tokens = @in, output_tokens = @out, updated_at = @updated
        WHERE id = @id;
        """;

    private const string AuditSql = """
        INSERT INTO fix_audit (fix_id, actor, event, detail) VALUES (@fix, @actor, @event, @detail::jsonb);
        """;

    private const string Columns =
        "id, issue_id, repo_full_name, status, provider, model, branch, pr_url, summary, created_at, updated_at, "
        + "merged_at, verify_status, verified_at, input_tokens, output_tokens";

    private const string GetSql = $"""
        SELECT {Columns}
        FROM fix_suggestions
        WHERE id = @id;
        """;

    private const string ListByIssueSql = $"""
        SELECT {Columns}
        FROM fix_suggestions
        WHERE issue_id = @issue
        ORDER BY created_at DESC;
        """;

    public async Task InsertAsync(FixSuggestion s, CancellationToken cancellationToken = default)
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
        cmd.Parameters.AddWithValue("summary", s.Summary);
        cmd.Parameters.AddWithValue("created", s.CreatedAt);
        cmd.Parameters.AddWithValue("updated", s.UpdatedAt);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateAsync(FixSuggestion s, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(UpdateSql, conn);
        cmd.Parameters.AddWithValue("id", s.Id);
        cmd.Parameters.AddWithValue("status", (short)s.Status);
        cmd.Parameters.AddWithValue("branch", s.Branch);
        cmd.Parameters.AddWithValue("pr", s.PrUrl);
        cmd.Parameters.AddWithValue("summary", s.Summary);
        cmd.Parameters.AddWithValue("in", s.InputTokens);
        cmd.Parameters.AddWithValue("out", s.OutputTokens);
        cmd.Parameters.AddWithValue("updated", s.UpdatedAt);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
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
        cmd.Parameters.AddWithValue("detail", detailJson);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<FixSuggestion?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(GetSql, conn);
        cmd.Parameters.AddWithValue("id", id);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Map(reader) : null;
    }

    // The Conductor's week for an org (ADR-0031), one grouped query joining runs to their issue's project:
    // proposed (created in window), draft PRs opened (created + succeeded — FixStatus 3), merged (merged_at in
    // window), auto-resolved (verify held — VerifyStatus 2 — verified_at in window).
    private const string WeeklyFixActivitySql = """
        SELECT
            count(*) FILTER (WHERE fs.created_at >= @start AND fs.created_at < @end) AS proposed,
            count(*) FILTER (WHERE fs.status = 3 AND fs.created_at >= @start AND fs.created_at < @end) AS prs_opened,
            count(*) FILTER (WHERE fs.merged_at >= @start AND fs.merged_at < @end) AS prs_merged,
            count(*) FILTER (WHERE fs.verify_status = 2 AND fs.verified_at >= @start AND fs.verified_at < @end) AS auto_resolved
        FROM fix_suggestions fs
        JOIN issues i ON i.id = fs.issue_id
        JOIN projects p ON p.id = i.project_id
        WHERE p.org_id = @org;
        """;

    /// <summary>The org's Conductor activity over [start, end) for the weekly digest (ADR-0031).</summary>
    public async Task<WeeklyFixActivity> WeeklyFixActivityAsync(
        long orgId, DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(WeeklyFixActivitySql, conn);
        cmd.Parameters.AddWithValue("org", orgId);
        cmd.Parameters.AddWithValue("start", start);
        cmd.Parameters.AddWithValue("end", end);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new WeeklyFixActivity(
            reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));
    }

    public async Task<IReadOnlyList<FixSuggestion>> ListByIssueAsync(
        long issueId, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(ListByIssueSql, conn);
        cmd.Parameters.AddWithValue("issue", issueId);
        var list = new List<FixSuggestion>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(Map(reader));
        }
        return list;
    }

    private static FixSuggestion Map(NpgsqlDataReader r) => new(
        r.GetGuid(0),
        r.GetInt64(1),
        r.GetString(2),
        (FixStatus)r.GetInt16(3),
        r.GetString(4),
        r.GetString(5),
        r.GetString(6),
        r.GetString(7),
        r.GetString(8),
        r.GetFieldValue<DateTimeOffset>(9),
        r.GetFieldValue<DateTimeOffset>(10))
    {
        MergedAt = r.IsDBNull(11) ? null : r.GetFieldValue<DateTimeOffset>(11),
        VerifyStatus = (VerifyStatus)r.GetInt16(12),
        VerifiedAt = r.IsDBNull(13) ? null : r.GetFieldValue<DateTimeOffset>(13),
        InputTokens = r.GetInt64(14),
        OutputTokens = r.GetInt64(15),
    };
}
