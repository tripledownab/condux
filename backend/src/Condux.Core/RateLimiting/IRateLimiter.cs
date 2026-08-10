namespace Condux.Core.RateLimiting;

/// <summary>
/// A per-key token-bucket rate limiter. The <paramref name="ratePerSecond"/> and
/// <paramref name="burst"/> are supplied per call so the relay can source them
/// from the project's plan tier at request time (see <c>PlanCatalog</c>). A rate
/// &lt;= 0 means unlimited.
///
/// Two implementations sit behind this interface: an in-process limiter
/// (<see cref="InMemoryRateLimiter"/>, for dev/tests and single-relay setups) and
/// a Valkey-backed one (for a budget shared across relay replicas).
/// </summary>
public interface IRateLimiter
{
    /// <summary>Consume one token for <paramref name="key"/> (typically a project id).</summary>
    ValueTask<RateLimitDecision> CheckAsync(
        string key, double ratePerSecond, long burst, CancellationToken ct = default);
}
