using Condux.Core.Auth;
using Condux.Core.Secrets;
using Condux.Storage.Postgres;

namespace Condux.ControlPlane.Auth;

/// <summary>The outcome of verifying a second factor, so the caller does not infer it from a bool.</summary>
internal enum MfaVerdict
{
    Accepted,
    Rejected,
    /// <summary>Too many failures. The caller must end the pending session, not just say no.</summary>
    Exhausted,
    /// <summary>The account is in its per-account cooldown, which no single session's counter can see.</summary>
    CooledDown,
}

/// <summary>
/// Enrolment and verification of TOTP second factors (ADR-0039).
/// </summary>
/// <remarks>
/// The secret is sealed with <see cref="SecretBox"/> rather than stored plaintext. It cannot be hashed,
/// because verification recomputes the expected code, so sealing is the only protection available; and
/// "the backups are encrypted" answers backup theft, not the read-only database access or SQL injection
/// that is the likelier way a table leaks.
/// </remarks>
internal sealed class MultiFactor(UserMfaRepository store, SessionRepository sessions, SecretBox? secrets)
{
    /// <summary>Failures against one pending session before it is ended and the password is required again.</summary>
    public const int MaxSessionAttempts = 5;

    /// <summary>Failures against one account before a cooldown, counted across sessions and addresses.</summary>
    public const int MaxAccountAttempts = 10;

    /// <summary>
    /// A cooldown, deliberately not a lock. A lock keyed on an account is a denial-of-service primitive:
    /// anyone who knows an email address could lock its owner out at will.
    /// </summary>
    public static readonly TimeSpan AccountCooldown = TimeSpan.FromMinutes(15);

    /// <summary>Whether MFA can be offered at all. False when no master key is configured.</summary>
    public bool Available => secrets is not null;

    public async Task<bool> IsEnabledAsync(long userId, CancellationToken ct = default) =>
        await store.GetAsync(userId, ct) is { ConfirmedAt: not null };

    /// <summary>Begins enrolment, returning the secret and the URI an authenticator scans.</summary>
    public async Task<(string Secret, string Uri)?> BeginEnrolmentAsync(
        long userId, string email, CancellationToken ct = default)
    {
        if (secrets is null)
        {
            return null;
        }

        var secret = Totp.NewSecret();
        if (!await store.EnrollAsync(userId, secrets.Seal(secret), ct))
        {
            return null; // already confirmed; disabling is the only way to replace a live factor
        }

        return (secret, Totp.ProvisioningUri(secret, email, "Condux"));
    }

    /// <summary>Activates an enrolled factor, returning the recovery codes to show exactly once.</summary>
    public async Task<string[]?> ConfirmAsync(long userId, string? code, CancellationToken ct = default)
    {
        if (secrets is null || await store.GetAsync(userId, ct) is not { ConfirmedAt: null } enrolment)
        {
            return null;
        }

        if (!Totp.TryValidate(secrets.Open(enrolment.SecretEncrypted), code, DateTimeOffset.UtcNow, out var step))
        {
            return null;
        }

        // Recovery codes are written BEFORE the factor is switched on. Enabling MFA is the step that
        // changes how the account can be reached, so it goes last: if storing the codes fails, the user
        // is simply still unenrolled, whereas the other order leaves MFA enforced with no way back in
        // if the device is then lost. Codes written against an enrolment that never confirms are inert
        // and are replaced wholesale by the next attempt.
        var (raw, hashes) = RecoveryCodes.Generate();
        await store.ReplaceRecoveryCodesAsync(userId, hashes, ct);

        if (!await store.ConfirmAsync(userId, ct))
        {
            return null;
        }

        await store.TryConsumeStepAsync(userId, step, ct);
        return raw;
    }

    /// <summary>Fresh recovery codes, invalidating every code the user held before.</summary>
    public async Task<string[]?> RegenerateRecoveryCodesAsync(long userId, CancellationToken ct = default)
    {
        if (!await IsEnabledAsync(userId, ct))
        {
            return null;
        }

        var (raw, hashes) = RecoveryCodes.Generate();
        await store.ReplaceRecoveryCodesAsync(userId, hashes, ct);
        return raw;
    }

    public Task DisableAsync(long userId, CancellationToken ct = default) => store.DisableAsync(userId, ct);

    public Task<int> RemainingRecoveryCodesAsync(long userId, CancellationToken ct = default) =>
        store.RemainingRecoveryCodesAsync(userId, ct);

    /// <summary>
    /// Verifies a TOTP code or a recovery code against a pending session.
    /// </summary>
    /// <remarks>
    /// One entry point for both, because the caller must not be able to tell them apart and neither must
    /// an attacker: a distinct "that was a recovery code" response confirms which kind of secret was
    /// guessed. Accepting a TOTP code goes through the store's guarded UPDATE, so a code replayed inside
    /// its own window is refused even though it computes correctly.
    /// </remarks>
    public async Task<MfaVerdict> VerifyAsync(
        long userId, string tokenHash, string? code, CancellationToken ct = default)
    {
        var enrolment = await store.GetAsync(userId, ct);
        if (secrets is null || enrolment is not { ConfirmedAt: not null })
        {
            return MfaVerdict.Rejected;
        }

        if (enrolment.LockedUntil is { } until && until > DateTimeOffset.UtcNow)
        {
            return MfaVerdict.CooledDown;
        }

        var accepted =
            (Totp.TryValidate(secrets.Open(enrolment.SecretEncrypted), code, DateTimeOffset.UtcNow, out var step)
             && await store.TryConsumeStepAsync(userId, step, ct))
            || (!string.IsNullOrWhiteSpace(code)
                && await store.TryRedeemRecoveryCodeAsync(
                    userId, SessionTokens.HashToken(RecoveryCodes.Normalize(code)), ct));

        if (accepted)
        {
            // Also clears it for a recovery-code success, which the TOTP path's own reset does not cover.
            await store.ClearFailuresAsync(userId, ct);
            return MfaVerdict.Accepted;
        }

        await store.RecordFailureAsync(userId, MaxAccountAttempts, AccountCooldown, ct);
        return await sessions.RecordMfaAttemptAsync(tokenHash, ct) >= MaxSessionAttempts
            ? MfaVerdict.Exhausted
            : MfaVerdict.Rejected;
    }
}
