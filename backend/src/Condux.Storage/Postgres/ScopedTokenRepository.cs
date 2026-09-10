using Npgsql;

namespace Condux.Storage.Postgres;

/// <summary>
/// One machine token, whatever it authorises. <c>ScopeId</c> is the project or org it belongs to; only the
/// hash is stored, so this row can be shown to an operator without exposing the secret.
/// <c>Capability</c> is 0 for every token kind whose table has no capability column. It has no default:
/// omitting it would quietly claim the lowest authority, and a row is only ever built from a query that
/// selected it.
/// </summary>
public sealed record ScopedTokenRow(
    Guid Id, long ScopeId, string Name,
    DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt, DateTimeOffset? RevokedAt, int Capability);

/// <summary>
/// A live token resolved from a presented hash: what it is scoped to, which row it is, and what it may do.
/// The token id is what an audit trail names when the actor is a machine rather than a user.
/// </summary>
public readonly record struct ScopedTokenIdentity(long ScopeId, Guid TokenId, int Capability);

/// <summary>
/// Mint, list, resolve and revoke, for every kind of machine token. The MCP and release repositories were
/// identical apart from their comments, and a third copy for runners would have repeated the drift that
/// already happened one level up, where three token types reached two secret encodings and two hash
/// casings before anyone noticed.
///
/// The table, scope column and capability column come from the calling type as compile-time constants and
/// are never user input, which is what makes composing them into SQL safe here. Every value is still a
/// parameter.
///
/// <paramref name="capabilityColumn"/> is null for a token kind whose authority is fixed by which endpoint
/// accepts it (release, runner). Those read a constant 0 and never see the column, so adding one to a
/// third kind does not touch them.
/// </summary>
public sealed class ScopedTokenRepository(
    string connectionString, string table, string scopeColumn, string? capabilityColumn = null)
{
    // Normalised to int either way: the column is SMALLINT, the constant is int4, and the reader must not
    // have to know which table it is looking at.
    private string CapabilitySelect => capabilityColumn is null ? "0" : $"{capabilityColumn}::int";

    private string InsertSql => capabilityColumn is null
        ? $"""
            INSERT INTO {table} (id, {scopeColumn}, token_hash, name)
            VALUES (@id, @scope, @hash, @name)
            RETURNING id, {scopeColumn}, name, created_at, last_used_at, revoked_at, {CapabilitySelect};
            """
        : $"""
            INSERT INTO {table} (id, {scopeColumn}, token_hash, name, {capabilityColumn})
            VALUES (@id, @scope, @hash, @name, @capability)
            RETURNING id, {scopeColumn}, name, created_at, last_used_at, revoked_at, {CapabilitySelect};
            """;

    private string ListSql => $"""
        SELECT id, {scopeColumn}, name, created_at, last_used_at, revoked_at, {CapabilitySelect}
        FROM {table} WHERE {scopeColumn} = @scope ORDER BY created_at DESC;
        """;

    // Resolve and stamp in one statement, so the liveness check and the usage stamp cannot interleave with
    // a revoke: a token revoked between the two would otherwise still resolve.
    private string ResolveSql => $"""
        UPDATE {table} SET last_used_at = now()
        WHERE token_hash = @hash AND revoked_at IS NULL
        RETURNING {scopeColumn}, id, {CapabilitySelect};
        """;

    private string RevokeSql =>
        $"UPDATE {table} SET revoked_at = now() WHERE {scopeColumn} = @scope AND id = @id AND revoked_at IS NULL;";

    public async Task<ScopedTokenRow> CreateAsync(
        long scopeId, string tokenHash, string name, int capability = 0, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(InsertSql, conn);
        cmd.Parameters.AddWithValue("id", Guid.CreateVersion7());
        cmd.Parameters.AddWithValue("scope", scopeId);
        cmd.Parameters.AddWithValue("hash", tokenHash);
        cmd.Parameters.AddWithValue("name", name);
        if (capabilityColumn is not null)
        {
            cmd.Parameters.AddWithValue("capability", (short)capability);
        }
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
    public async Task<long?> ResolveScopeAsync(string tokenHash, CancellationToken ct = default) =>
        await ResolveIdentityAsync(tokenHash, ct) is { } identity ? identity.ScopeId : null;

    /// <summary>
    /// The live token behind a presented hash, or null when unknown or revoked. Stamps last use in the
    /// same statement, so a revoke cannot interleave between the liveness check and the stamp.
    /// </summary>
    public async Task<ScopedTokenIdentity?> ResolveIdentityAsync(
        string tokenHash, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(ResolveSql, conn);
        cmd.Parameters.AddWithValue("hash", tokenHash);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new ScopedTokenIdentity(reader.GetInt64(0), reader.GetGuid(1), reader.GetInt32(2))
            : null;
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
        r.IsDBNull(5) ? null : r.GetFieldValue<DateTimeOffset>(5),
        r.GetInt32(6));
}
