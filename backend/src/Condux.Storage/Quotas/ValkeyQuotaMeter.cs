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
    // Read → take as much of the batch as fits → INCRBY → refresh TTL. Atomic under Valkey's
    // single-threaded execution, so concurrent replicas cannot over-admit between the read and the write.
    //   KEYS[1] = month counter key
    //   ARGV    = monthlyLimit, count, ttlSeconds
    //   returns = { admitted, used, remaining }
    private const string Script = """
        local limit = tonumber(ARGV[1])
        local want = tonumber(ARGV[2])
        local used = tonumber(redis.call('GET', KEYS[1]) or '0')
        local room = limit - used
        if room <= 0 then
          return { 0, used, 0 }
        end
        local take = want
        if take > room then take = room end
        used = redis.call('INCRBY', KEYS[1], take)
        redis.call('EXPIRE', KEYS[1], tonumber(ARGV[3]))
        local remaining = limit - used
        if remaining < 0 then remaining = 0 end
        return { take, used, remaining }
        """;

    // Give back what was admitted and then could not be stored, never below zero. The floor is what stops
    // a duplicate or late refund manufacturing quota, and it has to be inside the script: DECRBY alone is
    // atomic but unbounded, and clamping afterwards is a read-then-write two callers can interleave.
    // The key is not created when it is absent, since there would be nothing to give back.
    //   KEYS[1] = month counter key
    //   ARGV    = count
    private const string RefundScript = """
        local used = tonumber(redis.call('GET', KEYS[1]))
        if used == nil then
          return 0
        end
        local give = tonumber(ARGV[1])
        if give > used then give = used end
        return redis.call('DECRBY', KEYS[1], give)
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
        string key, long monthlyLimit, long count = 1, CancellationToken ct = default)
    {
        IQuotaMeter.RequirePositiveCount(count);

        // Unlimited tiers skip the round-trip entirely.
        if (monthlyLimit <= 0)
        {
            return new QuotaDecision(count, 0, long.MaxValue);
        }

        var monthKey = _prefix + key + ":" + _now().ToString("yyyyMM", CultureInfo.InvariantCulture);
        try
        {
            var result = (RedisResult[]?)await _db.ScriptEvaluateAsync(
                Script, [monthKey], [monthlyLimit, count, TtlSeconds]);

            if (result is null || result.Length < 3)
            {
                return new QuotaDecision(count, 0, long.MaxValue);
            }

            return new QuotaDecision((long)result[0], (long)result[1], (long)result[2]);
        }
        catch (RedisException)
        {
            // Fail open: a metering outage must not become an ingest outage.
            return new QuotaDecision(count, 0, long.MaxValue);
        }
    }

    public async ValueTask RefundAsync(string key, long count, CancellationToken ct = default)
    {
        IQuotaMeter.RequirePositiveCount(count);

        var monthKey = _prefix + key + ":" + _now().ToString("yyyyMM", CultureInfo.InvariantCulture);
        try
        {
            await _db.ScriptEvaluateAsync(RefundScript, [monthKey], [count]);
        }
        catch (RedisException)
        {
            // Swallowed for the same reason TryConsumeAsync fails open, but the direction differs and is
            // worth naming: this loses the caller its refund rather than granting anyone extra quota, and
            // the caller is already on its way to answering an error for the failure that led here.
        }
    }
}
