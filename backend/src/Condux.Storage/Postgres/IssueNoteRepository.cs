using Npgsql;

namespace Condux.Storage.Postgres;

/// <summary>A note on an issue. <c>AuthorEmail</c> is null when the author's account was deleted.</summary>
public sealed record IssueNote(
    Guid Id, long? AuthorUserId, string? AuthorEmail, string Body, DateTimeOffset CreatedAt);

/// <summary>Adds, lists, and removes collaborative notes on an issue (keyed by the internal issue id).</summary>
public sealed class IssueNoteRepository(string connectionString)
{
    // Insert then read the author's email back in one round trip, so every returned note carries who
    // wrote it (a LEFT JOIN so a since-deleted author yields a null email, not a dropped row).
    private const string InsertSql = """
        WITH inserted AS (
            INSERT INTO issue_notes (id, issue_id, author_user_id, body)
            VALUES (@id, @issue, @author, @body)
            RETURNING id, author_user_id, body, created_at
        )
        SELECT i.id, i.author_user_id, u.email, i.body, i.created_at
        FROM inserted i LEFT JOIN users u ON u.id = i.author_user_id;
        """;

    private const string ListSql = """
        SELECT n.id, n.author_user_id, u.email, n.body, n.created_at
        FROM issue_notes n LEFT JOIN users u ON u.id = n.author_user_id
        WHERE n.issue_id = @issue ORDER BY n.created_at;
        """;

    private const string GetSql = """
        SELECT n.id, n.author_user_id, u.email, n.body, n.created_at
        FROM issue_notes n LEFT JOIN users u ON u.id = n.author_user_id
        WHERE n.issue_id = @issue AND n.id = @id;
        """;

    private const string DeleteSql = "DELETE FROM issue_notes WHERE issue_id = @issue AND id = @id;";

    public async Task<IssueNote> AddAsync(long issueId, long authorUserId, string body, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(InsertSql, conn);
        cmd.Parameters.AddWithValue("id", Guid.CreateVersion7());
        cmd.Parameters.AddWithValue("issue", issueId);
        cmd.Parameters.AddWithValue("author", authorUserId);
        cmd.Parameters.AddWithValue("body", body);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return Read(reader);
    }

    public async Task<IReadOnlyList<IssueNote>> ListByIssueAsync(long issueId, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(ListSql, conn);
        cmd.Parameters.AddWithValue("issue", issueId);
        var list = new List<IssueNote>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(Read(reader));
        }
        return list;
    }

    /// <summary>The note if it belongs to this issue (else null) — used to authorize a delete.</summary>
    public async Task<IssueNote?> GetAsync(long issueId, Guid noteId, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(GetSql, conn);
        cmd.Parameters.AddWithValue("issue", issueId);
        cmd.Parameters.AddWithValue("id", noteId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    public async Task<bool> DeleteAsync(long issueId, Guid noteId, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(DeleteSql, conn);
        cmd.Parameters.AddWithValue("issue", issueId);
        cmd.Parameters.AddWithValue("id", noteId);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    private static IssueNote Read(NpgsqlDataReader r) => new(
        r.GetGuid(0),
        r.IsDBNull(1) ? null : r.GetInt64(1),
        r.IsDBNull(2) ? null : r.GetString(2),
        r.GetString(3),
        r.GetFieldValue<DateTimeOffset>(4));
}
