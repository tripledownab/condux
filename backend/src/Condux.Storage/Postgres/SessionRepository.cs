using Npgsql;

namespace Condux.Storage.Postgres;

/// <summary>A server-side session. Only <c>TokenHash</c> is stored; the raw token lives in the cookie.</summary>
public sealed record Session(
    long Id, long UserId, string TokenHash,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? RevokedAt);

/// <summary>Issues, resolves, and revokes server-side sessions.</summary>
public sealed class SessionRepository(string connectionString)
{
    private const string InsertSql = """
        INSERT INTO sessions (user_id, token_hash, expires_at)
        VALUES (@user, @hash, @expires)
        RETURNING id, user_id, token_hash, created_at, expires_at, revoked_at;
        """;

    // Resolve to the owning user only if the session is live (not revoked, not expired).
    private const string ActiveUserSql = """
        SELECT u.id, u.email, u.password_hash, u.created_at, u.onboarded_at
        FROM sessions s JOIN users u ON u.id = s.user_id
        WHERE s.token_hash = @hash AND s.revoked_at IS NULL AND s.expires_at > now();
        """;

    private const string RevokeSql =
        "UPDATE sessions SET revoked_at = now() WHERE token_hash = @hash AND revoked_at IS NULL;";

    public async Task<Session> CreateAsync(
        long userId, string tokenHash, DateTimeOffset expiresAt, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(InsertSql, conn);
        cmd.Parameters.AddWithValue("user", userId);
        cmd.Parameters.AddWithValue("hash", tokenHash);
        cmd.Parameters.AddWithValue("expires", expiresAt);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return Read(reader);
    }

    /// <summary>Returns the user for a live session, or null if the token is unknown, expired, or revoked.</summary>
    public async Task<User?> GetActiveUserAsync(string tokenHash, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(ActiveUserSql, conn);
        cmd.Parameters.AddWithValue("hash", tokenHash);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new User(reader.GetInt64(0), reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2), // null for federated (Google) accounts
                reader.GetFieldValue<DateTimeOffset>(3),
                reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4))
            : null;
    }

    /// <summary>Revokes a session by its token hash; returns false if it was unknown or already revoked.</summary>
    public async Task<bool> RevokeAsync(string tokenHash, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(RevokeSql, conn);
        cmd.Parameters.AddWithValue("hash", tokenHash);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    private static Session Read(NpgsqlDataReader r) => new(
        r.GetInt64(0), r.GetInt64(1), r.GetString(2),
        r.GetFieldValue<DateTimeOffset>(3), r.GetFieldValue<DateTimeOffset>(4),
        r.IsDBNull(5) ? null : r.GetFieldValue<DateTimeOffset>(5));
}
