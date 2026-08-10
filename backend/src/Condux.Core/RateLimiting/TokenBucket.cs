namespace Condux.Core.RateLimiting;

/// <summary>
/// The pure token-bucket step: given the current bucket state and a limit
/// (<paramref name="ratePerSecond"/> tokens/sec up to <paramref name="burst"/>),
/// refill by elapsed time and try to consume one token at
/// <paramref name="nowSeconds"/>.
///
/// This is the single source of truth for the limiter math. Both the in-memory
/// limiters (<see cref="InMemoryRateLimiter"/>, <see cref="SpikeGuard"/>) and the
/// Valkey Lua script implement the same algorithm, so their behavior matches. It
/// is a pure function (no clock, no state) so refill logic is deterministically
/// testable.
/// </summary>
public static class TokenBucket
{
    /// <summary>Bucket contents: <paramref name="Tokens"/> available as of <paramref name="UpdatedAt"/> (seconds).</summary>
    public readonly record struct State(double Tokens, double UpdatedAt);

    /// <summary>Refill for the elapsed time, then attempt to consume one token.</summary>
    public static (State Next, RateLimitDecision Decision) Step(
        State current, double ratePerSecond, double burst, double nowSeconds)
    {
        // rate <= 0 means "unlimited" (e.g. Enterprise custom): never limit, never mutate.
        if (ratePerSecond <= 0)
        {
            return (current, new RateLimitDecision(true, long.MaxValue, 0));
        }

        var tokens = current.Tokens;
        var updatedAt = current.UpdatedAt;

        var elapsed = nowSeconds - updatedAt;
        if (elapsed > 0)
        {
            tokens = Math.Min(burst, tokens + elapsed * ratePerSecond);
            updatedAt = nowSeconds;
        }

        var allowed = tokens >= 1.0;
        if (allowed)
        {
            tokens -= 1.0;
        }

        var remaining = (long)Math.Max(0, Math.Floor(tokens));
        // Seconds until enough tokens exist to admit the next request.
        var retryAfter = allowed ? 0 : (long)Math.Ceiling((1.0 - tokens) / ratePerSecond);
        return (new State(tokens, updatedAt), new RateLimitDecision(allowed, remaining, retryAfter));
    }
}
