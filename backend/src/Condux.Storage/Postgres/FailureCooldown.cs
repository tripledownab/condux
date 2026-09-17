namespace Condux.Storage.Postgres;

/// <summary>
/// Counting failed credential attempts against an account, and the cooldown that follows. Every table
/// that throttles a guessable secret builds its statements here rather than writing its own.
/// </summary>
/// <remarks>
/// <para>
/// Two tables use it today: <c>user_mfa</c> for second-factor codes and <c>users</c> for passwords. They
/// cannot share a counter, because a <c>user_mfa</c> row exists only for an account that enrolled a
/// factor, so a password throttle reading that table would silently do nothing for every account without
/// one. They can and must share the statement, which is where the subtlety lives.
/// </para>
/// <para>
/// The counter is WINDOWED, not lifetime. A plain running total looks equivalent and is not: once it has
/// ever reached the limit it stays there, so every later mistyped credential re-trips the cooldown and
/// what was described as a cooldown becomes an effectively permanent hair-trigger lock on a real user's
/// account. A lapsed cooldown therefore starts the count again from this failure.
/// </para>
/// <para>
/// <b><c>FOR UPDATE</c> is load-bearing and must not be removed.</b> One statement is not by itself
/// enough: a CTE is evaluated against the snapshot taken when the statement began, so without the lock
/// two overlapping failures both compute "1" and the second overwrites the first with the same value.
/// Measured on PostgreSQL 16 against this exact statement: two forced-overlapping executions counted 1
/// instead of 2, and ten concurrent ones counted 6 instead of 10. Taking the row lock in the CTE makes
/// the second execution wait and then re-read the committed row, so it counts from the new value.
/// </para>
/// <para>
/// This is the read-then-update race, and writing it as one statement hid it rather than removing it.
/// The counter it silently inflates is an attempt allowance, so an attacker issuing guesses in parallel
/// gets more of them than the limit says.
/// </para>
/// </remarks>
public static class FailureCooldown
{
    /// <summary>
    /// Which table and columns hold one account's failure state. Every field is a compile-time constant
    /// held by the repository that owns the table, never a value from a request: these are interpolated
    /// as SQL identifiers, which no parameter can stand in for.
    /// </summary>
    internal readonly record struct Columns(string Table, string Key, string Attempts, string LockedUntil);

    /// <summary>
    /// Counts one failure, starting a cooldown once <c>@max</c> is reached within the window.
    /// Parameters: <c>@subject</c>, <c>@max</c>, <c>@cooldown</c>.
    /// </summary>
    internal static string Record(Columns c) => $"""
        WITH next AS (
            SELECT {c.Key} AS subject,
                   CASE WHEN {c.LockedUntil} IS NOT NULL AND {c.LockedUntil} <= now()
                        THEN 1 ELSE {c.Attempts} + 1 END AS attempts
            FROM {c.Table} WHERE {c.Key} = @subject FOR UPDATE
        )
        UPDATE {c.Table} t
        SET {c.Attempts} = next.attempts,
            {c.LockedUntil} = CASE WHEN next.attempts >= @max
                                   THEN now() + @cooldown ELSE t.{c.LockedUntil} END
        FROM next WHERE t.{c.Key} = next.subject;
        """;

    /// <summary>Clears the failure state after a success. Parameter: <c>@subject</c>.</summary>
    internal static string Clear(Columns c) =>
        $"UPDATE {c.Table} SET {ClearAssignments(c)} WHERE {c.Key} = @subject;";

    /// <summary>
    /// The SET assignments that clear the state, for a statement that must do it as part of something
    /// else. Setting a new password is that case: it has to land in the same statement as the password,
    /// or a reset leaves the account cooled down and the new password appears wrong.
    /// </summary>
    internal static string ClearAssignments(Columns c) => $"{c.Attempts} = 0, {c.LockedUntil} = NULL";

    /// <summary>
    /// Whether a cooldown is still running. The caller supplies the instant, so the boundary is read
    /// against the application clock while <see cref="Record"/> wrote it from the database clock: skew
    /// between the two shifts the end of a cooldown by the skew, and nothing here can narrow that.
    /// </summary>
    public static bool IsCoolingDown(DateTimeOffset? lockedUntil, DateTimeOffset now) =>
        lockedUntil is { } until && until > now;
}
