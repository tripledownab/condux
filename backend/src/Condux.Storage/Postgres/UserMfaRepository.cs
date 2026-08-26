using Npgsql;

namespace Condux.Storage.Postgres;

/// <summary>A user's second factor. The secret is a sealed SecretBox blob, never plaintext.</summary>
public sealed record UserMfa(
    long UserId, byte[] SecretEncrypted, DateTimeOffset? ConfirmedAt,
    long? LastUsedStep, int FailedAttempts, DateTimeOffset? LockedUntil);

/// <summary>
/// Enrolment, verification and recovery codes for TOTP multi-factor authentication.
/// </summary>
/// <remarks>
/// Every decision here is a single guarded UPDATE whose ROW COUNT is the answer, never a read followed
/// by a write. That is not style. A read-then-update replay guard passes every sequential test and then
/// admits the same code from several concurrent requests at once, because each one reads the old value
/// before any of them writes.
/// </remarks>
public sealed class UserMfaRepository(string connectionString)
{
    private const string GetSql = """
        SELECT user_id, secret_encrypted, confirmed_at, last_used_step, failed_attempts, locked_until
        FROM user_mfa WHERE user_id = @user;
        """;

    // Enrolling replaces an UNCONFIRMED secret only. Without that guard, two concurrent enrolments could
    // both write, and worse, an enrolment could silently reset a factor the user is already relying on.
    private const string EnrollSql = """
        INSERT INTO user_mfa (user_id, secret_encrypted) VALUES (@user, @secret)
        ON CONFLICT (user_id) DO UPDATE
          SET secret_encrypted = EXCLUDED.secret_encrypted, last_used_step = NULL,
              failed_attempts = 0, locked_until = NULL
          WHERE user_mfa.confirmed_at IS NULL;
        """;

    private const string ConfirmSql = """
        UPDATE user_mfa SET confirmed_at = now(), failed_attempts = 0, locked_until = NULL
        WHERE user_id = @user AND confirmed_at IS NULL;
        """;

    // The replay guard, RFC 6238 section 5.2. The check IS the write: accepting a step strictly greater
    // than the last one both records it and refuses a repeat, atomically. Strictly greater rather than
    // merely different, because with a one-step drift window equality alone would still admit the
    // previous step's code.
    private const string ConsumeStepSql = """
        UPDATE user_mfa SET last_used_step = @step, failed_attempts = 0, locked_until = NULL
        WHERE user_id = @user AND confirmed_at IS NOT NULL
          AND (last_used_step IS NULL OR last_used_step < @step);
        """;

    // Per-account failure counting, which the per-session counter cannot do: an attacker spreading
    // guesses across fresh sessions and addresses looks like a series of first attempts to it. Counted
    // and tripped in one statement so concurrent failures cannot each read a stale total.
    // The counter is WINDOWED, not lifetime. A plain running total looks equivalent and is not: once it
    // has ever reached the limit it stays there, so every later mistyped code re-trips the cooldown and
    // what was described as a cooldown becomes an effectively permanent hair-trigger lock on a real
    // user's account. A lapsed cooldown therefore starts the count again from this failure.
    private const string RecordFailureSql = """
        WITH next AS (
            SELECT user_id,
                   CASE WHEN locked_until IS NOT NULL AND locked_until <= now()
                        THEN 1 ELSE failed_attempts + 1 END AS attempts
            FROM user_mfa WHERE user_id = @user
        )
        UPDATE user_mfa m
        SET failed_attempts = next.attempts,
            locked_until = CASE WHEN next.attempts >= @max THEN now() + @cooldown ELSE m.locked_until END
        FROM next WHERE m.user_id = next.user_id;
        """;

    private const string ClearFailuresSql =
        "UPDATE user_mfa SET failed_attempts = 0, locked_until = NULL WHERE user_id = @user;";

    private const string DeleteSql = "DELETE FROM user_mfa WHERE user_id = @user;";

    // Recovery codes are deleted explicitly rather than left to a cascade: they cascade from users, not
    // from user_mfa, so disabling MFA would otherwise leave them behind as a way back in.
    private const string DeleteCodesSql = "DELETE FROM user_mfa_recovery_codes WHERE user_id = @user;";

    private const string InsertCodeSql =
        "INSERT INTO user_mfa_recovery_codes (user_id, code_hash) VALUES (@user, @hash);";

    // Single use, decided by the row count, so two concurrent redemptions of one code cannot both win.
    private const string RedeemCodeSql = """
        UPDATE user_mfa_recovery_codes SET used_at = now()
        WHERE user_id = @user AND code_hash = @hash AND used_at IS NULL;
        """;

    private const string RemainingCodesSql =
        "SELECT count(*) FROM user_mfa_recovery_codes WHERE user_id = @user AND used_at IS NULL;";

    public async Task<UserMfa?> GetAsync(long userId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(GetSql, conn);
        cmd.Parameters.AddWithValue("user", userId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    /// <summary>Stores an unconfirmed secret. False when a confirmed factor already exists.</summary>
    public async Task<bool> EnrollAsync(long userId, byte[] sealedSecret, CancellationToken ct = default) =>
        await ExecuteAsync(EnrollSql, ct, ("user", userId), ("secret", sealedSecret)) == 1;

    /// <summary>Activates an enrolled secret. False if there was nothing unconfirmed to activate.</summary>
    public async Task<bool> ConfirmAsync(long userId, CancellationToken ct = default) =>
        await ExecuteAsync(ConfirmSql, ct, ("user", userId)) == 1;

    /// <summary>
    /// Accepts a verified step, refusing one already used. False means replay, so the caller must reject
    /// the code even though it computed correctly.
    /// </summary>
    public async Task<bool> TryConsumeStepAsync(long userId, long step, CancellationToken ct = default) =>
        await ExecuteAsync(ConsumeStepSql, ct, ("user", userId), ("step", step)) == 1;

    /// <summary>
    /// Counts a failed verification against the account, starting a cooldown once the limit is reached.
    /// A cooldown rather than a lock, so knowing an email address is not enough to lock someone out.
    /// </summary>
    public Task RecordFailureAsync(
        long userId, int maxAttempts, TimeSpan cooldown, CancellationToken ct = default) =>
        ExecuteAsync(RecordFailureSql, ct,
            ("user", userId), ("max", maxAttempts), ("cooldown", cooldown));

    /// <summary>
    /// Clears the failure state after any successful verification. Separate from the TOTP path's own
    /// reset because redeeming a recovery code is also a success, and leaving the count standing there
    /// would punish the user for the very situation recovery codes exist to rescue.
    /// </summary>
    public Task ClearFailuresAsync(long userId, CancellationToken ct = default) =>
        ExecuteAsync(ClearFailuresSql, ct, ("user", userId));

    /// <summary>Removes the factor and its recovery codes together.</summary>
    public async Task DisableAsync(long userId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var transaction = await conn.BeginTransactionAsync(ct);
        foreach (var sql in new[] { DeleteCodesSql, DeleteSql })
        {
            await using var cmd = new NpgsqlCommand(sql, conn, transaction);
            cmd.Parameters.AddWithValue("user", userId);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    /// <summary>Replaces the whole set, so regenerating invalidates every code the user held before.</summary>
    public async Task ReplaceRecoveryCodesAsync(
        long userId, IReadOnlyList<string> hashes, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var transaction = await conn.BeginTransactionAsync(ct);
        await using (var clear = new NpgsqlCommand(DeleteCodesSql, conn, transaction))
        {
            clear.Parameters.AddWithValue("user", userId);
            await clear.ExecuteNonQueryAsync(ct);
        }

        foreach (var hash in hashes)
        {
            await using var insert = new NpgsqlCommand(InsertCodeSql, conn, transaction);
            insert.Parameters.AddWithValue("user", userId);
            insert.Parameters.AddWithValue("hash", hash);
            await insert.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    /// <summary>Spends one recovery code. False if it is unknown or already used.</summary>
    public async Task<bool> TryRedeemRecoveryCodeAsync(
        long userId, string codeHash, CancellationToken ct = default) =>
        await ExecuteAsync(RedeemCodeSql, ct, ("user", userId), ("hash", codeHash)) == 1;

    public async Task<int> RemainingRecoveryCodesAsync(long userId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(RemainingCodesSql, conn);
        cmd.Parameters.AddWithValue("user", userId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    private async Task<int> ExecuteAsync(
        string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private static UserMfa Read(NpgsqlDataReader r) => new(
        r.GetInt64(0), (byte[])r[1],
        r.IsDBNull(2) ? null : r.GetFieldValue<DateTimeOffset>(2),
        r.IsDBNull(3) ? null : r.GetInt64(3),
        r.GetInt32(4),
        r.IsDBNull(5) ? null : r.GetFieldValue<DateTimeOffset>(5));
}
