using Npgsql;

namespace Condux.Storage.Postgres;

/// <summary>A password reset token, resolved from the hash a redemption presents.</summary>
public sealed record PasswordReset(long UserId);

/// <summary>
/// Password reset tokens (migration 0048). Built like <see cref="OrgInviteRepository"/>: an opaque token
/// mailed to an address, stored only as a hash, redeemable once before it expires.
///
/// Every condition that decides whether a token is still good lives in SQL, not in a caller's predicate.
/// A read-then-check would let two redemptions of the same token both pass the check before either
/// wrote, which is exactly the race a single-use token exists to prevent.
/// </summary>
public sealed class PasswordResetRepository(string connectionString)
{
    private const string CreateSql = """
        INSERT INTO password_resets (user_id, token_hash, expires_at)
        VALUES (@user, @hash, @expires);
        """;

    // Redeeming is the guarded UPDATE itself: the row count is the answer, so a second redemption of the
    // same token matches nothing rather than racing the first.
    private const string RedeemSql = """
        UPDATE password_resets SET used_at = now()
        WHERE token_hash = @hash AND used_at IS NULL AND expires_at > now()
        RETURNING user_id;
        """;

    private const string RecentCountSql = """
        SELECT count(*) FROM password_resets
        WHERE user_id = @user AND created_at > now() - @window::interval;
        """;

    public async Task CreateAsync(
        long userId, string tokenHash, DateTimeOffset expiresAt, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(CreateSql, conn);
        cmd.Parameters.AddWithValue("user", userId);
        cmd.Parameters.AddWithValue("hash", tokenHash);
        cmd.Parameters.AddWithValue("expires", expiresAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Spends the token and returns whose it was, or null when it is unknown, already used or expired.
    /// The three are one answer on purpose: telling them apart tells a caller which guesses were closer.
    /// </summary>
    public async Task<PasswordReset?> RedeemAsync(string tokenHash, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(RedeemSql, conn);
        cmd.Parameters.AddWithValue("hash", tokenHash);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new PasswordReset(reader.GetInt64(0))
            : null;
    }

    /// <summary>How many resets this user asked for inside the window, for the request throttle.</summary>
    public async Task<int> CountSinceAsync(
        long userId, TimeSpan window, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(RecentCountSql, conn);
        cmd.Parameters.AddWithValue("user", userId);
        cmd.Parameters.AddWithValue("window", $"{(int)window.TotalSeconds} seconds");
        return (int)(long)(await cmd.ExecuteScalarAsync(ct))!;
    }
}
