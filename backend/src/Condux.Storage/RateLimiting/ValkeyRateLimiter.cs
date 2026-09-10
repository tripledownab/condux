using Condux.Core.RateLimiting;
using StackExchange.Redis;

namespace Condux.Storage.RateLimiting;

/// <summary>
/// Cross-instance token-bucket limiter backed by Valkey (Redis-compatible). Every
/// relay replica shares one budget per key (project), enforced atomically by a Lua
/// script so concurrent checks across replicas can't over-admit. The script uses
/// the Valkey server clock (<c>TIME</c>), so relay instances need no synchronized
/// clocks. On a Valkey outage it <b>fails open</b> — a limiter blip must never drop
/// ingest.
/// </summary>
public sealed class ValkeyRateLimiter : IRateLimiter
{
    // Atomic refill-then-consume. A second implementation of Condux.Core TokenBucket.Step, in
    // another language, so the two are driven over the same sequence and compared decision by
    // decision (pinned by ValkeyRateLimiterTest).
    //   KEYS[1] = bucket key
    //   ARGV    = ratePerSecond, burst, requested
    //   returns = { allowed(0|1), remaining, retryAfterSeconds }
    private const string Script = """
        local rate = tonumber(ARGV[1])
        local burst = tonumber(ARGV[2])
        local requested = tonumber(ARGV[3])

        local t = redis.call('TIME')
        local now = tonumber(t[1]) + tonumber(t[2]) / 1000000

        local data = redis.call('HMGET', KEYS[1], 'tokens', 'ts')
        local tokens = tonumber(data[1])
        local ts = tonumber(data[2])
        if tokens == nil then
          tokens = burst
          ts = now
        end

        local elapsed = now - ts
        if elapsed > 0 then
          tokens = math.min(burst, tokens + elapsed * rate)
          ts = now
        end

        local allowed = 0
        if tokens >= requested then
          allowed = 1
          tokens = tokens - requested
        end

        redis.call('HSET', KEYS[1], 'tokens', tokens, 'ts', ts)
        -- Let an idle bucket expire once it would have fully refilled.
        redis.call('EXPIRE', KEYS[1], math.ceil(burst / rate) + 1)

        local remaining = math.floor(tokens)
        local retry = 0
        if allowed == 0 then
          retry = math.ceil((requested - tokens) / rate)
        end
        return { allowed, remaining, retry }
        """;

    private readonly IDatabase _db;
    private readonly string _prefix;

    public ValkeyRateLimiter(IConnectionMultiplexer redis, string prefix = "condux:rl:")
    {
        _db = redis.GetDatabase();
        _prefix = prefix;
    }

    public async ValueTask<RateLimitDecision> CheckAsync(
        string key, double ratePerSecond, long burst, CancellationToken ct = default)
    {
        // Unlimited tiers skip the round-trip entirely.
        if (ratePerSecond <= 0)
        {
            return new RateLimitDecision(true, long.MaxValue, 0);
        }

        try
        {
            var result = (RedisResult[]?)await _db.ScriptEvaluateAsync(
                Script,
                [_prefix + key],
                [ratePerSecond, burst, 1]);

            if (result is null || result.Length < 3)
            {
                return new RateLimitDecision(true, 0, 0);
            }

            var allowed = (long)result[0] == 1;
            return new RateLimitDecision(allowed, (long)result[1], (long)result[2]);
        }
        catch (RedisException)
        {
            // Fail open: a limiter outage must not become an ingest outage.
            return new RateLimitDecision(true, 0, 0);
        }
    }
}
