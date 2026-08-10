using System.Globalization;
using Condux.Core.Quotas;
using StackExchange.Redis;

namespace Condux.Storage.Quotas;

/// <summary>
/// Cross-instance monthly quota meter backed by Valkey (Redis-compatible). Every relay replica shares one
/// counter per (project, month). A Lua script makes the read-limit-then-increment atomic, so concurrent
/// events across replicas can't over-admit, and only an admitted event increments (usage never overshoots).
/// On a Valkey outage it <b>fails open</b> — a metering blip must never drop ingest.
/// </summary>
public sealed class ValkeyQuotaMeter : IQuotaMeter
{
    // Read → reject if at limit → INCR → refresh TTL. Atomic under Valkey's single-threaded execution.
    //   KEYS[1] = month counter key
    //   ARGV    = monthlyLimit, ttlSeconds
    //   returns = { allowed(0|1), used, remaining }
    private const string Script = """
        local limit = tonumber(ARGV[1])
        local used = tonumber(redis.call('GET', KEYS[1]) or '0')
        if used >= limit then
          return { 0, used, 0 }
        end
        used = redis.call('INCR', KEYS[1])
        redis.call('EXPIRE', KEYS[1], tonumber(ARGV[2]))
        local remaining = limit - used
        if remaining < 0 then remaining = 0 end
        return { 1, used, remaining }
        """;

    // A month counter only needs to outlive its month; ~40 days self-cleans it well after rollover.
    private const int TtlSeconds = 40 * 24 * 60 * 60;

    private readonly IDatabase _db;
    private readonly string _prefix;
    private readonly Func<DateTimeOffset> _now;

    public ValkeyQuotaMeter(
        IConnectionMultiplexer redis, string prefix = "condux:quota:", Func<DateTimeOffset>? clock = null)
    {
        _db = redis.GetDatabase();
        _prefix = prefix;
        _now = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public async ValueTask<QuotaDecision> TryConsumeAsync(
        string key, long monthlyLimit, CancellationToken ct = default)
    {
        // Unlimited tiers skip the round-trip entirely.
        if (monthlyLimit <= 0)
        {
            return new QuotaDecision(true, 0, long.MaxValue);
        }

        var monthKey = _prefix + key + ":" + _now().ToString("yyyyMM", CultureInfo.InvariantCulture);
        try
        {
            var result = (RedisResult[]?)await _db.ScriptEvaluateAsync(
                Script, [monthKey], [monthlyLimit, TtlSeconds]);

            if (result is null || result.Length < 3)
            {
                return new QuotaDecision(true, 0, long.MaxValue);
            }

            return new QuotaDecision((long)result[0] == 1, (long)result[1], (long)result[2]);
        }
        catch (RedisException)
        {
            // Fail open: a metering outage must not become an ingest outage.
            return new QuotaDecision(true, 0, long.MaxValue);
        }
    }
}
