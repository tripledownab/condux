using Npgsql;

namespace Condux.Storage.Postgres;

/// <summary>
/// A note on an issue. <c>AuthorEmail</c> is null when the author's account was deleted, and both author
/// fields are null on a note whose author is gone. <c>AuthorTokenName</c> names the MCP token that wrote
/// the note when an agent did (ADR-0046); a note has a user author or a token author, never both.
///
/// <c>Body</c> is free text a human or an agent supplied, stored raw and unescaped on purpose: a note
/// explaining a bug legitimately contains angle brackets, quotes and code, so it is encoded at each sink
/// rather than mangled on the way in. Today the only sink is the notes endpoint rendering through React,
/// which escapes. ANY NEW SINK MUST ENCODE: the email notifiers build HTML and already encode their other
/// error-derived text, and the Conductor prompt would put it in front of a model that opens pull requests.
/// </summary>
public sealed record IssueNote(
    Guid Id, long? AuthorUserId, string? AuthorEmail, string? AuthorTokenName,
    string Body, DateTimeOffset CreatedAt);

/// <summary>
/// Who wrote a note: a signed-in user, or an MCP token acting for an agent. The constructor is private so
/// the factories are the only way to name an author, which is what makes "never both" hold here and not
/// only in the table's CHECK. (A <c>default</c> value names neither, which the table also allows: it is
/// the state a note reaches when its author deletes their account.)
/// </summary>
public readonly record struct NoteAuthor
{
    private NoteAuthor(long? userId, Guid? mcpTokenId)
    {
        UserId = userId;
        McpTokenId = mcpTokenId;
    }

    public long? UserId { get; }

    public Guid? McpTokenId { get; }

    public static NoteAuthor User(long userId) => new(userId, null);

    public static NoteAuthor McpToken(Guid tokenId) => new(null, tokenId);
}

/// <summary>Adds, lists, and removes collaborative notes on an issue (keyed by the internal issue id).</summary>
public sealed class IssueNoteRepository(string connectionString)
{
    // Every read resolves both possible authors in the same round trip. LEFT JOINs throughout, so a
    // since-deleted user or a note with no author at all yields nulls rather than a dropped row.
    // The token id itself is not selected: nothing reads it, and the name is what a reader needs. The
    // column still carries the link, so a query that ever wants it can join from there.
    private const string SelectColumns =
        "n.id, n.author_user_id, u.email, t.name, n.body, n.created_at";

    private const string AuthorJoins = """
        LEFT JOIN users u ON u.id = n.author_user_id
        LEFT JOIN mcp_tokens t ON t.id = n.author_mcp_token_id
        """;

    private const string InsertSql = $"""
        WITH inserted AS (
            INSERT INTO issue_notes (id, issue_id, author_user_id, author_mcp_token_id, body)
            VALUES (@id, @issue, @author, @token, @body)
            RETURNING id, author_user_id, author_mcp_token_id, body, created_at
        )
        SELECT {SelectColumns}
        FROM inserted n {AuthorJoins};
        """;

    private const string ListSql = $"""
        SELECT {SelectColumns}
        FROM issue_notes n {AuthorJoins}
        WHERE n.issue_id = @issue ORDER BY n.created_at;
        """;

    private const string GetSql = $"""
        SELECT {SelectColumns}
        FROM issue_notes n {AuthorJoins}
        WHERE n.issue_id = @issue AND n.id = @id;
        """;

    private const string DeleteSql = "DELETE FROM issue_notes WHERE issue_id = @issue AND id = @id;";

    public async Task<IssueNote> AddAsync(
        long issueId, NoteAuthor author, string body, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(InsertSql, conn);
        cmd.Parameters.AddWithValue("id", Guid.CreateVersion7());
        cmd.Parameters.AddWithValue("issue", issueId);
        cmd.Parameters.AddWithValue("author", (object?)author.UserId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("token", (object?)author.McpTokenId ?? DBNull.Value);
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
        r.IsDBNull(3) ? null : r.GetString(3),
        r.GetString(4),
        r.GetFieldValue<DateTimeOffset>(5));
}
