using Npgsql;

namespace Condux.Storage.Postgres;

/// <summary>
/// The password-guessing counter for an account. A partial of <see cref="UserRepository"/> rather than a
/// repository of its own, because these columns are on <c>users</c> and one table keeps one home.
/// </summary>
public sealed partial class UserRepository
{
    // The statement is built in FailureCooldown, which the second-factor counter in user_mfa also
    // uses. The two cannot share a COUNTER: a user_mfa row exists only for an account that enrolled a
    // factor, so reading it would leave every account without one unthrottled. They share the statement
    // instead, which is where the windowed-restart rule lives.
    // A property, not a field, and that is load-bearing rather than style. SetPasswordSql lives in the
    // other half of this partial class and interpolates these names, and static FIELD initialisers run in
    // an order the compiler chooses across partial files. As a field this read as the default struct,
    // whose names are all null, producing SQL that failed at runtime with no compiler complaint: every
    // password change and every password reset answered 500. A property is evaluated on access, so no
    // ordering can reach it.
    private static FailureCooldown.Columns LoginFailures =>
        new("users", "id", "failed_logins", "login_locked_until");

    private static readonly string RecordLoginFailureSql = FailureCooldown.Record(LoginFailures);

    private static readonly string ClearLoginFailuresSql = FailureCooldown.Clear(LoginFailures);

    /// <summary>
    /// Counts one wrong password against the account, starting a cooldown once the limit is reached.
    /// Shared by signing in and by re-authenticating, so an attacker cannot reset the count by moving
    /// between the two.
    /// </summary>
    public async Task RecordLoginFailureAsync(
        long id, int maxAttempts, TimeSpan cooldown, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(RecordLoginFailureSql, conn);
        cmd.Parameters.AddWithValue("subject", id);
        cmd.Parameters.AddWithValue("max", maxAttempts);
        cmd.Parameters.AddWithValue("cooldown", cooldown);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Clears the count after a correct password. Without this a user who mistypes a few times and then
    /// succeeds stays one typo away from a cooldown for as long as the window lasts.
    /// </summary>
    public async Task ClearLoginFailuresAsync(long id, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(ClearLoginFailuresSql, conn);
        cmd.Parameters.AddWithValue("subject", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
