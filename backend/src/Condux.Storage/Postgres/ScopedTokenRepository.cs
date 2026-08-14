using Npgsql;

namespace Condux.Storage.Postgres;

/// <summary>
/// One machine token, whatever it authorises. <c>ScopeId</c> is the project or org it belongs to; only the
/// hash is stored, so this row can be shown to an operator without exposing the secret.
/// </summary>
public sealed record ScopedTokenRow(
    Guid Id, long ScopeId, string Name,
    DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt, DateTimeOffset? RevokedAt);

/// <summary>
/// Mint, list, resolve and revoke, for every kind of machine token. The MCP and release repositories were
/// identical apart from their comments, and a third copy for runners would have repeated the drift that
/// already happened one level up, where three token types reached two secret encodings and two hash
/// casings before anyone noticed.
///
/// The table and scope column come from the calling type as compile-time constants and are never user
/// input, which is what makes composing them into SQL safe here. Every value is still a parameter.
/// </summary>
public sealed class ScopedTokenRepository(string connectionString, string table, string scopeColumn)
{
    private string InsertSql => $"""
        INSERT INTO {table} (id, {scopeColumn}, token_hash, name)
        VALUES (@id, @scope, @hash, @name)
        RETURNING id, {scopeColumn}, name, created_at, last_used_at, revoked_at;
        """;

    private string ListSql => $"""
        SELECT id, {scopeColumn}, name, created_at, last_used_at, revoked_at
        FROM {table} WHERE {scopeColumn} = @scope ORDER BY created_at DESC;
        """;

    // Resolve and stamp in one statement, so the liveness check and the usage stamp cannot interleave with
    // a revoke: a token revoked between the two would otherwise still resolve.
    private string ResolveSql => $"""
        UPDATE {table} SET last_used_at = now()
        WHERE token_hash = @hash AND revoked_at IS NULL
        RETURNING {scopeColumn};
        """;

    private string RevokeSql =>
        $"UPDATE {table} SET revoked_at = now() WHERE {scopeColumn} = @scope AND id = @id AND revoked_at IS NULL;";

    public async Task<ScopedTokenRow> CreateAsync(
        long scopeId, string tokenHash, string name, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(InsertSql, conn);
        cmd.Parameters.AddWithValue("id", Guid.CreateVersion7());
        cmd.Parameters.AddWithValue("scope", scopeId);
        cmd.Parameters.AddWithValue("hash", tokenHash);
        cmd.Parameters.AddWithValue("name", name);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return Read(reader);
    }

    public async Task<IReadOnlyList<ScopedTokenRow>> ListAsync(long scopeId, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(ListSql, conn);
        cmd.Parameters.AddWithValue("scope", scopeId);
        var list = new List<ScopedTokenRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(Read(reader));
        }

        return list;
    }

    /// <summary>The scope a live token belongs to, or null when unknown or revoked. Stamps last use.</summary>
    public async Task<long?> ResolveScopeAsync(string tokenHash, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(ResolveSql, conn);
        cmd.Parameters.AddWithValue("hash", tokenHash);
        return await cmd.ExecuteScalarAsync(ct) is long scopeId ? scopeId : null;
    }

    public async Task<bool> RevokeAsync(long scopeId, Guid id, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(RevokeSql, conn);
        cmd.Parameters.AddWithValue("scope", scopeId);
        cmd.Parameters.AddWithValue("id", id);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    private static ScopedTokenRow Read(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetInt64(1), r.GetString(2), r.GetFieldValue<DateTimeOffset>(3),
        r.IsDBNull(4) ? null : r.GetFieldValue<DateTimeOffset>(4),
        r.IsDBNull(5) ? null : r.GetFieldValue<DateTimeOffset>(5));
}
