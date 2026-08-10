namespace Condux.Core.RateLimiting;

/// <summary>
/// In-process token-bucket limiter — the default, used in dev/tests and
/// single-relay deployments. When several relay instances run behind a load
/// balancer, use the Valkey-backed limiter instead so the budget is shared;
/// otherwise each instance enforces the limit independently. Thread-safe.
/// </summary>
public sealed class InMemoryRateLimiter : IRateLimiter
{
    private readonly Dictionary<string, TokenBucket.State> _buckets = new();
    private readonly Lock _gate = new();
    private readonly Func<double> _clock;

    /// <param name="clock">
    /// Monotonic seconds source; defaults to process uptime. Injected in tests so
    /// refill behavior is deterministic.
    /// </param>
    public InMemoryRateLimiter(Func<double>? clock = null) =>
        _clock = clock ?? (() => Environment.TickCount64 / 1000.0);

    public ValueTask<RateLimitDecision> CheckAsync(
        string key, double ratePerSecond, long burst, CancellationToken ct = default)
    {
        // Unlimited tiers never touch the bucket map.
        if (ratePerSecond <= 0)
        {
            return ValueTask.FromResult(new RateLimitDecision(true, long.MaxValue, 0));
        }

        var now = _clock();
        lock (_gate)
        {
            var state = _buckets.TryGetValue(key, out var existing)
                ? existing
                : new TokenBucket.State(burst, now); // new buckets start full
            var (next, decision) = TokenBucket.Step(state, ratePerSecond, burst, now);
            _buckets[key] = next;
            return ValueTask.FromResult(decision);
        }
    }
}
