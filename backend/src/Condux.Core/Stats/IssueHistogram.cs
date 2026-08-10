namespace Condux.Core.Stats;

/// <summary>One histogram bucket: its start (unix seconds, UTC) and the exact event count in it.</summary>
public sealed record HistogramBucket(long Ts, long Count);

/// <summary>
/// Builds the zero-filled time series a chart renders from the sparse buckets the rollup returns:
/// every bucket in the window is present (0 when nothing happened), aligned to the bucket size, ending
/// at the bucket containing <c>now</c>. Hourly buckets up to 7 days; daily for longer windows (the
/// rollup is hourly, so daily is an exact re-aggregation).
/// </summary>
public static class IssueHistogram
{
    private const int HourSeconds = 3_600;
    private const int DaySeconds = 86_400;
    private const int MaxHourlyWindowHours = 168;

    public static int BucketSeconds(int windowHours) =>
        windowHours <= MaxHourlyWindowHours ? HourSeconds : DaySeconds;

    /// <summary>Zero-fill sparse (ts → count) pairs into the complete window series.</summary>
    public static IReadOnlyList<HistogramBucket> Build(
        IReadOnlyDictionary<long, long> sparse, DateTimeOffset nowUtc, int windowHours)
    {
        var step = BucketSeconds(windowHours);
        var end = nowUtc.ToUnixTimeSeconds() / step * step;
        var start = end - ((long)windowHours * HourSeconds / step - 1) * step;

        var buckets = new List<HistogramBucket>();
        for (var ts = start; ts <= end; ts += step)
        {
            buckets.Add(new HistogramBucket(ts, sparse.TryGetValue(ts, out var count) ? count : 0));
        }

        return buckets;
    }
}
