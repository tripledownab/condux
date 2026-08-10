using Npgsql;

namespace Condux.Storage.Postgres;

/// <summary>A scoped MCP token. Only <c>TokenHash</c> is stored; the raw token is shown once.</summary>
public sealed record McpToken(
    Guid Id, long ProjectId, string Name,
    DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt, DateTimeOffset? RevokedAt);

/// <summary>Mints, lists, resolves, and revokes scoped MCP tokens (an AI agent reads a project's issues
/// with them over <c>POST /api/mcp</c>). Mirrors <see cref="ReleaseTokenRepository"/>.</summary>
public sealed class McpTokenRepository(string connectionString)
{
    private const string InsertSql = """
        INSERT INTO mcp_tokens (id, project_id, token_hash, name)
        VALUES (@id, @project, @hash, @name)
        RETURNING id, project_id, name, created_at, last_used_at, revoked_at;
        """;

    private const string ListSql = """
        SELECT id, project_id, name, created_at, last_used_at, revoked_at
        FROM mcp_tokens WHERE project_id = @project ORDER BY created_at DESC;
        """;

    // Resolve a presented token to its project only if live (not revoked); stamp last_used_at so the
    // dashboard can show when the agent last used it. One statement, so the check + stamp are atomic.
    private const string ResolveSql = """
        UPDATE mcp_tokens SET last_used_at = now()
        WHERE token_hash = @hash AND revoked_at IS NULL
        RETURNING project_id;
        """;

    private const string RevokeSql =
        "UPDATE mcp_tokens SET revoked_at = now() WHERE project_id = @project AND id = @id AND revoked_at IS NULL;";

    public async Task<McpToken> CreateAsync(
        long projectId, string tokenHash, string name, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(InsertSql, conn);
        cmd.Parameters.AddWithValue("id", Guid.CreateVersion7());
        cmd.Parameters.AddWithValue("project", projectId);
        cmd.Parameters.AddWithValue("hash", tokenHash);
        cmd.Parameters.AddWithValue("name", name);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return Read(reader);
    }

    public async Task<IReadOnlyList<McpToken>> ListByProjectAsync(long projectId, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(ListSql, conn);
        cmd.Parameters.AddWithValue("project", projectId);
        var list = new List<McpToken>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(Read(reader));
        }
        return list;
    }

    /// <summary>The project a live token belongs to (null if unknown or revoked); stamps last_used_at.</summary>
    public async Task<long?> ResolveProjectAsync(string tokenHash, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(ResolveSql, conn);
        cmd.Parameters.AddWithValue("hash", tokenHash);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is long projectId ? projectId : null;
    }

    public async Task<bool> RevokeAsync(long projectId, Guid id, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(RevokeSql, conn);
        cmd.Parameters.AddWithValue("project", projectId);
        cmd.Parameters.AddWithValue("id", id);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    private static McpToken Read(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetInt64(1), r.GetString(2), r.GetFieldValue<DateTimeOffset>(3),
        r.IsDBNull(4) ? null : r.GetFieldValue<DateTimeOffset>(4),
        r.IsDBNull(5) ? null : r.GetFieldValue<DateTimeOffset>(5));
}
