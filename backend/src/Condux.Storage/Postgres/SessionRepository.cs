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
        INSERT INTO sessions (user_id, token_hash, expires_at, mfa_pending)
        VALUES (@user, @hash, @expires, @pending)
        RETURNING id, user_id, token_hash, created_at, expires_at, revoked_at;
        """;

    // Resolve to the owning user only if the session is live (not revoked, not expired) AND has passed
    // the second factor.
    //
    // `NOT mfa_pending` is the whole MFA gate, and it lives here on purpose. This is the single place a
    // session becomes an identity, so a half-authenticated one resolves to nobody EVERYWHERE at once. The
    // alternative, checking at each endpoint, protects only the endpoints somebody remembered.
    private const string ActiveUserSql = """
        SELECT u.id, u.email, u.password_hash, u.created_at, u.onboarded_at
        FROM sessions s JOIN users u ON u.id = s.user_id
        WHERE s.token_hash = @hash AND s.revoked_at IS NULL AND s.expires_at > now()
          AND NOT s.mfa_pending;
        """;

    // The exact inverse, for the one endpoint that acts on a half-authenticated session. Splitting it
    // into a second named method rather than a boolean parameter makes "reads a pending session" a
    // greppable property of exactly one route.
    private const string PendingUserSql = """
        SELECT u.id, u.email, u.password_hash, u.created_at, u.onboarded_at
        FROM sessions s JOIN users u ON u.id = s.user_id
        WHERE s.token_hash = @hash AND s.revoked_at IS NULL AND s.expires_at > now()
          AND s.mfa_pending;
        """;

    // Promote in place: same row, same cookie, so the browser is never handed a second token and there
    // is no window where two live sessions exist for one sign-in. Extends the short pending expiry to a
    // full session at the same time.
    private const string CompleteMfaSql = """
        UPDATE sessions SET mfa_pending = false, mfa_attempts = 0, expires_at = @expires
        WHERE token_hash = @hash AND mfa_pending AND revoked_at IS NULL AND expires_at > now();
        """;

    // Row count is the answer, so concurrent attempts cannot each read a stale count and all pass.
    private const string RecordAttemptSql = """
        UPDATE sessions SET mfa_attempts = mfa_attempts + 1
        WHERE token_hash = @hash AND mfa_pending AND revoked_at IS NULL
        RETURNING mfa_attempts;
        """;

    private const string RevokeAllExceptSql = """
        UPDATE sessions SET revoked_at = now()
        WHERE user_id = @user AND revoked_at IS NULL AND token_hash <> @keep;
        """;

    private const string RevokeSql =
        "UPDATE sessions SET revoked_at = now() WHERE token_hash = @hash AND revoked_at IS NULL;";

    /// <param name="mfaPending">
    /// True when the password succeeded but the second factor has not. The cookie is issued either way;
    /// what changes is that the session resolves to nobody until <see cref="CompleteMfaAsync"/> promotes it.
    /// </param>
    public async Task<Session> CreateAsync(
        long userId, string tokenHash, DateTimeOffset expiresAt, CancellationToken ct = default,
        bool mfaPending = false)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(InsertSql, conn);
        cmd.Parameters.AddWithValue("user", userId);
        cmd.Parameters.AddWithValue("hash", tokenHash);
        cmd.Parameters.AddWithValue("expires", expiresAt);
        cmd.Parameters.AddWithValue("pending", mfaPending);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return Read(reader);
    }

    /// <summary>Returns the user for a live session, or null if the token is unknown, expired, or revoked.</summary>
    public async Task<User?> GetActiveUserAsync(string tokenHash, CancellationToken ct = default) =>
        await ReadUserAsync(ActiveUserSql, tokenHash, ct);

    private async Task<User?> ReadUserAsync(string sql, string tokenHash, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("hash", tokenHash);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new User(reader.GetInt64(0), reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2), // null for federated (Google) accounts
                reader.GetFieldValue<DateTimeOffset>(3),
                reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4))
            : null;
    }

    /// <summary>The user behind a session that has passed the password but NOT the second factor.</summary>
    public async Task<User?> GetPendingUserAsync(string tokenHash, CancellationToken ct = default) =>
        await ReadUserAsync(PendingUserSql, tokenHash, ct);

    /// <summary>Promotes a pending session to fully authenticated. False if it was not pending or has lapsed.</summary>
    public async Task<bool> CompleteMfaAsync(
        string tokenHash, DateTimeOffset expiresAt, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(CompleteMfaSql, conn);
        cmd.Parameters.AddWithValue("hash", tokenHash);
        cmd.Parameters.AddWithValue("expires", expiresAt);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    /// <summary>Counts a failed attempt against the pending session; returns the new total.</summary>
    public async Task<int> RecordMfaAttemptAsync(string tokenHash, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(RecordAttemptSql, conn);
        cmd.Parameters.AddWithValue("hash", tokenHash);
        return await cmd.ExecuteScalarAsync(ct) is int attempts ? attempts : 0;
    }

    /// <summary>
    /// Revokes every other live session for a user, keeping the caller's own. Used when the factor
    /// changes, because sessions opened under the previous rule must not outlive the change.
    /// </summary>
    public async Task<int> RevokeAllExceptAsync(
        long userId, string keepTokenHash, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(RevokeAllExceptSql, conn);
        cmd.Parameters.AddWithValue("user", userId);
        cmd.Parameters.AddWithValue("keep", keepTokenHash);
        return await cmd.ExecuteNonQueryAsync(ct);
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
