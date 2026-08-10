using Npgsql;

namespace Condux.Storage.Postgres;

/// <summary>Per-user "new issues" nav badge state (ADR-0030): a per (user, project) watermark of when the
/// user last opened the project's issue list, and the count of issues activated (new or regressed) since
/// then. Mirrors the fixes badge's per-user viewed model, but a single watermark per project rather than
/// a row per issue — a badge only needs "since when".</summary>
public sealed class IssueSeenRepository(string connectionString)
{
    // Count issues whose activation (first seen, or the reopen on a regression) is newer than the user's
    // watermark. No watermark row yet => compare against epoch, so every issue reads as new until the user
    // first opens the list.
    private const string CountSql = """
        SELECT count(*)
        FROM issues i
        LEFT JOIN issue_seen s ON s.user_id = @user AND s.project_id = i.project_id
        WHERE i.project_id = @project
          AND i.activated_at > COALESCE(s.seen_at, 'epoch'::timestamptz);
        """;

    private const string MarkSeenSql = """
        INSERT INTO issue_seen (user_id, project_id, seen_at)
        VALUES (@user, @project, now())
        ON CONFLICT (user_id, project_id) DO UPDATE SET seen_at = now();
        """;

    /// <summary>How many issues are new or regressed since this user last opened the project's list.</summary>
    public async Task<int> CountNewAsync(long userId, long projectId, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(CountSql, conn);
        cmd.Parameters.AddWithValue("user", userId);
        cmd.Parameters.AddWithValue("project", projectId);
        return (int)(long)(await cmd.ExecuteScalarAsync(cancellationToken) ?? 0L);
    }

    /// <summary>Mark the project's issue list as seen now for this user (clears the badge). Idempotent.</summary>
    public async Task MarkSeenAsync(long userId, long projectId, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(MarkSeenSql, conn);
        cmd.Parameters.AddWithValue("user", userId);
        cmd.Parameters.AddWithValue("project", projectId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
