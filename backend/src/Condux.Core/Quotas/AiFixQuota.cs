namespace Condux.Core.Quotas;

/// <summary>
/// The per-org AI-fix allowance state (#100): a live counter reserved against at request time instead of
/// counting run rows on every check. Reservation semantics: <see cref="TryConsumeAsync"/> atomically takes
/// one run from the org's current calendar month (rolling the period over when the month changed) and
/// <see cref="RefundAsync"/> gives it back when the run fails or is cancelled — so in-flight requests are
/// counted (no over-consumption race) and failed runs stay free. Postgres-backed in production; the
/// audit/run rows remain the reconciliation truth.
/// </summary>
public interface IAiFixQuota
{
    /// <summary>Reserve one run for the org. <paramref name="monthlyLimit"/> 0 means uncapped (usage is
    /// still tracked for display). False when the allowance is spent.</summary>
    Task<bool> TryConsumeAsync(
        long orgId, int monthlyLimit, DateTimeOffset nowUtc, CancellationToken cancellationToken = default);

    /// <summary>Return one run to the org's current period (a failed/cancelled run delivered no PR).</summary>
    Task RefundAsync(long orgId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default);

    /// <summary>How many runs the org has consumed in the current period (0 when untouched) — the cheap
    /// read behind the usage meter.</summary>
    Task<int> GetUsedAsync(long orgId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default);

    /// <summary>Reserve one run from the org's one-time lifetime grant (#112) — a counter that never
    /// resets, independent of the monthly one, so the Free tier gets a fixed taste of the Conductor.
    /// <paramref name="lifetimeLimit"/> is the grant size (0 = none). False when it is spent.</summary>
    Task<bool> TryConsumeLifetimeAsync(
        long orgId, int lifetimeLimit, DateTimeOffset nowUtc, CancellationToken cancellationToken = default);

    /// <summary>Return one run to the org's lifetime grant when the job never reached the topic (the
    /// enqueue path). A run that starts then fails is refunded per-period by the orchestrator, which does
    /// not distinguish lifetime, so a failed lifetime run costs its slot — acceptable for a small grant.</summary>
    Task RefundLifetimeAsync(long orgId, CancellationToken cancellationToken = default);

    /// <summary>How many of the lifetime grant the org has consumed (0 when untouched).</summary>
    Task<int> GetLifetimeUsedAsync(long orgId, CancellationToken cancellationToken = default);
}
