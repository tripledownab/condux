using Condux.Core.Auth;
using Condux.Storage.Postgres;

namespace Condux.ControlPlane.Auth;

/// <summary>
/// The one place a password is checked, counted and refused. Signing in and re-authenticating both go
/// through it, so they share a counter.
/// </summary>
/// <remarks>
/// <para>
/// Sharing matters as much as counting. Before this, neither path counted anything, and throttling only
/// the sign-in would have moved the guessing to <c>/api/auth/mfa/enroll</c>, which re-asks for the same
/// password and which <see cref="Endpoints.MfaEndpoints"/> describes as the takeover path. Two counters
/// would grant an attacker one allowance per surface.
/// </para>
/// <para>
/// <b>The answer carries no verdict on purpose.</b> One that named the cooldown separately from a wrong
/// password would be an oracle waiting for a caller to surface it, and no caller needs the difference:
/// every refusal here is a 401.
/// </para>
/// </remarks>
internal static class PasswordGate
{
    /// <summary>
    /// Wrong passwords against one account before a cooldown starts.
    /// </summary>
    /// <remarks>
    /// Lower than <see cref="MultiFactor.MaxAccountAttempts"/>, and the difference is deliberate. A
    /// six-digit code drawn from a fresh window every thirty seconds cannot be guessed at any rate we
    /// would allow, so a generous limit there costs nothing. A password may be weak, so this limit is the
    /// thing bounding the grind. Five leaves room to mistype and halves the sustained rate.
    /// </remarks>
    public const int MaxAttempts = 5;

    /// <summary>
    /// A cooldown, deliberately not a lock. A lock keyed on an account is a denial-of-service primitive:
    /// anyone who knows an email address could hold its owner out at will.
    /// </summary>
    public static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The account, when this password may be accepted for it right now. Null for a wrong password, an
    /// account in its cooldown, an address with no account, and a federated account that has no password.
    /// </summary>
    /// <param name="user">The account, or null when the caller supplied an address that matches none.</param>
    public static async Task<User?> VerifyAsync(
        User? user, string? password, UserRepository users, CancellationToken ct = default)
    {
        // Derived on EVERY path, before any decision, including the ones that cannot succeed. The cost of
        // a key derivation dwarfs everything else here, so branching before it would let an attacker read
        // "no such account" or "cooling down" off a stopwatch while the status code said nothing.
        var correct = PasswordHasher.VerifyOrDecoy(password ?? string.Empty, user?.PasswordHash);

        if (user is null)
        {
            return null;
        }

        // Nothing is counted during a cooldown. More attempts would gain an attacker nothing, and a
        // legitimate user retrying must not extend their own wait. A correct password is refused too, or
        // the counter is decorative: guessing right on attempt six would simply win.
        if (FailureCooldown.IsCoolingDown(user.LoginLockedUntil, DateTimeOffset.UtcNow))
        {
            return null;
        }

        if (correct)
        {
            await users.ClearLoginFailuresAsync(user.Id, ct);
            return user;
        }

        await users.RecordLoginFailureAsync(user.Id, MaxAttempts, Cooldown, ct);
        return null;
    }
}
