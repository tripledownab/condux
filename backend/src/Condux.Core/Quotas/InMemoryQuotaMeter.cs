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

    public ValueTask<QuotaDecision> TryConsumeAsync(
        string key, long monthlyLimit, long count = 1, CancellationToken ct = default)
    {
        IQuotaMeter.RequirePositiveCount(count);

        if (monthlyLimit <= 0)
        {
            return ValueTask.FromResult(new QuotaDecision(count, 0, long.MaxValue));
        }

        var bucket = $"{key}:{now():yyyyMM}";
        while (true)
        {
            var used = counts.GetOrAdd(bucket, 0);
            var room = monthlyLimit - used;
            if (room <= 0)
            {
                return ValueTask.FromResult(new QuotaDecision(0, used, 0));
            }

            // Admit as much of the batch as fits rather than all-or-nothing: a batch that straddles the cap
            // must spend what is left, or the last events of the month are lost to whatever arrived in a
            // large enough group.
            var take = Math.Min(count, room);
            // Increment only by what is admitted; retry if another thread moved the count under us.
            if (counts.TryUpdate(bucket, used + take, used))
            {
                return ValueTask.FromResult(new QuotaDecision(take, used + take, monthlyLimit - (used + take)));
            }
        }
    }

    public ValueTask RefundAsync(string key, long count, CancellationToken ct = default)
    {
        IQuotaMeter.RequirePositiveCount(count);

        var bucket = $"{key}:{now():yyyyMM}";
        while (true)
        {
            // Absent means the month has no counter, so there is nothing to give back. Adding one here to
            // subtract from would write a negative that the next consume would then treat as free room.
            if (!counts.TryGetValue(bucket, out var used))
            {
                return ValueTask.CompletedTask;
            }
            var refunded = Math.Max(0, used - count);
            if (counts.TryUpdate(bucket, refunded, used))
            {
                return ValueTask.CompletedTask;
            }
        }
    }
}
