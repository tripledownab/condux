using Condux.Core.Events;
using Xunit;

namespace Condux.Core.Tests;

/// <summary>
/// An event is filed under its own time only within the project's retention before its receipt and a
/// minute after it; anything else, absent included, is filed at its receipt.
/// </summary>
public class EventTimesTests
{
    private static readonly DateTimeOffset Received = DateTimeOffset.FromUnixTimeSeconds(1790985600);
    private const int Retention = 30;

    private static DateTimeOffset Filed(DateTimeOffset claimed) =>
        EventTimes.ToFiledTime(claimed.ToUnixTimeMilliseconds(), Received, Retention);

    [Fact]
    public void Keeps_a_time_inside_the_window_including_both_edges()
    {
        Assert.Equal(Received.AddHours(-5), Filed(Received.AddHours(-5)));
        Assert.Equal(Received.AddDays(-Retention), Filed(Received.AddDays(-Retention)));
        Assert.Equal(Received + EventTimes.MaxAhead, Filed(Received + EventTimes.MaxAhead));
    }

    /// <summary>Sentry's own figure, written out rather than read from the constant it pins.</summary>
    [Fact]
    public void Allows_sixty_seconds_ahead_and_no_more()
    {
        Assert.Equal(Received.AddSeconds(60), Filed(Received.AddSeconds(60)));
        Assert.Equal(Received, Filed(Received.AddSeconds(61)));
    }

    [Fact]
    public void Files_a_time_just_past_either_edge_at_its_receipt()
    {
        Assert.Equal(Received, Filed(Received + EventTimes.MaxAhead + TimeSpan.FromMilliseconds(1)));
        Assert.Equal(Received, Filed(Received.AddDays(-Retention).AddMilliseconds(-1)));
    }

    /// <summary>
    /// Past 2106 a ClickHouse DateTime wraps, so a sender could aim counts at any hour. Year 9999 is the
    /// largest time the parser accepts.
    /// </summary>
    [Fact]
    public void Files_a_time_past_what_storage_can_hold_at_its_receipt()
    {
        Assert.Equal(Received, Filed(new DateTimeOffset(2500, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        Assert.Equal(Received, EventTimes.ToFiledTime(DateTimeOffset.MaxValue.ToUnixTimeMilliseconds(), Received, Retention));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(long.MinValue)]
    public void Files_an_absent_or_negative_time_at_its_receipt(long claimed) =>
        Assert.Equal(Received, EventTimes.ToFiledTime(claimed, Received, Retention));

    /// <summary>
    /// A retention long enough to reach back before 1970 would otherwise admit an absent time as 1970;
    /// the retention comes from a message header, so it is not assumed to be small.
    /// </summary>
    [Fact]
    public void Files_an_absent_time_at_its_receipt_whatever_the_retention()
    {
        Assert.Equal(Received, EventTimes.ToFiledTime(0, Received, ushort.MaxValue));
        // Past TimeSpan's range, which must not throw in the consumer's drain loop.
        Assert.Equal(Received, EventTimes.ToFiledTime(0, Received, int.MaxValue));
    }

    [Fact]
    public void Bounds_the_past_by_the_retention_it_is_given()
    {
        var old = Received.AddDays(-45);

        Assert.Equal(Received, EventTimes.ToFiledTime(old.ToUnixTimeMilliseconds(), Received, 30));
        Assert.Equal(old, EventTimes.ToFiledTime(old.ToUnixTimeMilliseconds(), Received, 90));
    }
}
