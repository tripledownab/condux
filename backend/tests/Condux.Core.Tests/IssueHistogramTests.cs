using Condux.Core.Stats;
using Xunit;

namespace Condux.Core.Tests;

public class IssueHistogramTests
{
    // 2026-07-20 10:30:00 UTC — mid-hour, so alignment is visible.
    private static readonly DateTimeOffset Now = new(2026, 7, 20, 10, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Builds_a_complete_hourly_series_ending_at_the_current_hour()
    {
        var tenOclock = new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        var sparse = new Dictionary<long, long>
        {
            [tenOclock] = 7,
            [tenOclock - 3 * 3600] = 2,
        };

        var buckets = IssueHistogram.Build(sparse, Now, windowHours: 24);

        Assert.Equal(24, buckets.Count);
        Assert.Equal(tenOclock, buckets[^1].Ts); // ends at the bucket containing now
        Assert.Equal(7, buckets[^1].Count);
        Assert.Equal(2, buckets[^4].Count);
        Assert.Equal(0, buckets[0].Count); // untouched hours are zero-filled, not missing
        Assert.All(buckets.Zip(buckets.Skip(1)), pair => Assert.Equal(3600, pair.Second.Ts - pair.First.Ts));
    }

    [Fact]
    public void Uses_daily_buckets_for_the_30_day_window()
    {
        Assert.Equal(3600, IssueHistogram.BucketSeconds(24));
        Assert.Equal(3600, IssueHistogram.BucketSeconds(168));
        Assert.Equal(86_400, IssueHistogram.BucketSeconds(720));

        var today = new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        var buckets = IssueHistogram.Build(new Dictionary<long, long> { [today] = 3 }, Now, windowHours: 720);

        Assert.Equal(30, buckets.Count);
        Assert.Equal(today, buckets[^1].Ts); // aligned to UTC midnight
        Assert.Equal(3, buckets[^1].Count);
    }

    [Fact]
    public void An_empty_rollup_still_yields_the_full_zeroed_window()
    {
        var buckets = IssueHistogram.Build(new Dictionary<long, long>(), Now, windowHours: 168);

        Assert.Equal(168, buckets.Count);
        Assert.All(buckets, b => Assert.Equal(0, b.Count));
    }
}
