using System.Collections.Concurrent;

namespace Condux.Core.Quotas;

/// <summary>
/// In-process monthly quota meter, keyed by (project, calendar month). Only admitted events increment the
/// counter, so usage never runs past the limit. Single-relay/dev/tests only — a multi-replica deployment
/// shares one budget via the Valkey meter. The clock is injectable so tests can exercise month rollover.
/// </summary>
public sealed class InMemoryQuotaMeter(Func<DateTimeOffset>? clock = null) : IQuotaMeter
{
    private readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly ConcurrentDictionary<string, long> counts = new();

    public ValueTask<QuotaDecision> TryConsumeAsync(string key, long monthlyLimit, CancellationToken ct = default)
    {
        if (monthlyLimit <= 0)
        {
            return ValueTask.FromResult(new QuotaDecision(true, 0, long.MaxValue));
        }

        var bucket = $"{key}:{now():yyyyMM}";
        while (true)
        {
            var used = counts.GetOrAdd(bucket, 0);
            if (used >= monthlyLimit)
            {
                return ValueTask.FromResult(new QuotaDecision(false, used, 0));
            }

            // Increment only on admission; retry if another thread moved the count under us.
            if (counts.TryUpdate(bucket, used + 1, used))
            {
                return ValueTask.FromResult(new QuotaDecision(true, used + 1, monthlyLimit - (used + 1)));
            }
        }
    }
}
