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

    // Sentry's clock drift correction (ClockDriftProcessor in relay-event-normalization at 9e49155): an
    // envelope's sent_at that differs from receipt by 55 minutes or more shifts every time in the event.
    private static Event At(DateTimeOffset claimed, params DateTimeOffset[] breadcrumbs) => new()
    {
        TimestampUnixMs = claimed.ToUnixTimeMilliseconds(),
        Breadcrumbs = [.. breadcrumbs.Select(b => new Breadcrumb { TimestampUnixMs = b.ToUnixTimeMilliseconds() })],
    };

    private static Event FiledEvent(Event e, DateTimeOffset? sentAt) =>
        EventTimes.ToFiledEvent(e, Received, Retention, sentAt?.ToUnixTimeMilliseconds() ?? 0);

    private static DateTimeOffset Time(long unixMs) => DateTimeOffset.FromUnixTimeMilliseconds(unixMs);

    [Fact]
    public void Shifts_a_slow_clock_forward_by_its_drift_with_its_breadcrumbs()
    {
        var e = At(Received.AddHours(-1).AddMinutes(-5), Received.AddHours(-1).AddMinutes(-6));

        var filed = FiledEvent(e, sentAt: Received.AddHours(-1));

        Assert.Equal(Received.AddMinutes(-5), Time(filed.TimestampUnixMs));
        Assert.Equal(Received.AddMinutes(-6), Time(filed.Breadcrumbs[0].TimestampUnixMs));
    }

    [Fact]
    public void Leaves_a_drift_under_55_minutes_alone_and_shifts_one_of_exactly_55()
    {
        var e = At(Received.AddHours(-2));

        Assert.Equal(Received.AddHours(-2), Time(FiledEvent(e, sentAt: Received.AddMinutes(-54)).TimestampUnixMs));
        Assert.Equal(
            Received.AddHours(-2).AddMinutes(55),
            Time(FiledEvent(e, sentAt: Received - EventTimes.MinClockDrift).TimestampUnixMs));
    }

    /// <summary>A fast clock's buffered events used to collapse onto their receipt, losing their order.</summary>
    [Fact]
    public void Shifts_a_fast_clock_back_so_buffered_events_keep_their_order()
    {
        var sentAt = Received.AddHours(3);
        var first = FiledEvent(At(Received.AddHours(2)), sentAt);
        var second = FiledEvent(At(Received.AddHours(2).AddMinutes(10)), sentAt);

        Assert.Equal(Received.AddHours(-1), Time(first.TimestampUnixMs));
        Assert.Equal(Received.AddMinutes(-50), Time(second.TimestampUnixMs));

        var crumb = FiledEvent(At(Received.AddHours(2), Received.AddHours(2).AddMinutes(-1)), sentAt);
        Assert.Equal(Received.AddHours(-1).AddMinutes(-1), Time(crumb.Breadcrumbs[0].TimestampUnixMs));
    }

    /// <summary>Only 0 means absent, for the event's time and for sent_at alike. A time before 1970 is
    /// implausible but still a time, so a correction to it moves the breadcrumbs too.</summary>
    [Fact]
    public void Treats_a_time_before_1970_as_a_time()
    {
        var e = new Event
        {
            TimestampUnixMs = -60_000,
            Breadcrumbs = [new Breadcrumb { TimestampUnixMs = -90_000 }],
        };

        var filed = FiledEvent(e, sentAt: null);

        Assert.Equal(Received, Time(filed.TimestampUnixMs));
        Assert.Equal(Received.AddSeconds(-30), Time(filed.Breadcrumbs[0].TimestampUnixMs));
        // A sent_at before 1970 still reads as a drift: decades, which the bounds then send to receipt.
        Assert.Equal(
            Received,
            Time(FiledEvent(At(Received.AddHours(-2)), sentAt: DateTimeOffset.UnixEpoch.AddDays(-1)).TimestampUnixMs));
    }

    /// <summary>A device whose clock reset to near 1970 sends an event with no time: it is filed at its
    /// receipt, never at receipt minus whatever the drift leaves of an absent time.</summary>
    [Fact]
    public void Files_an_absent_time_at_receipt_whatever_the_drift()
    {
        var e = new Event { TimestampUnixMs = 0 };

        Assert.Equal(Received, Time(FiledEvent(e, sentAt: DateTimeOffset.UnixEpoch.AddDays(10)).TimestampUnixMs));
    }

    [Fact]
    public void Moves_breadcrumbs_with_an_event_the_bounds_file_at_receipt()
    {
        var e = At(Received.AddDays(-45), Received.AddDays(-45).AddSeconds(-30));

        var filed = FiledEvent(e, sentAt: null);

        Assert.Equal(Received, Time(filed.TimestampUnixMs));
        Assert.Equal(Received.AddSeconds(-30), Time(filed.Breadcrumbs[0].TimestampUnixMs));
    }

    [Fact]
    public void Shifts_breadcrumbs_by_the_drift_alone_when_the_event_has_no_time()
    {
        var e = At(Received, Received.AddHours(-2)) with { TimestampUnixMs = 0 };

        var filed = FiledEvent(e, sentAt: Received.AddHours(-1));

        Assert.Equal(Received, Time(filed.TimestampUnixMs));
        Assert.Equal(Received.AddHours(-1), Time(filed.Breadcrumbs[0].TimestampUnixMs));
    }

    [Fact]
    public void Leaves_absent_breadcrumb_times_absent_and_drops_one_shifted_out_of_range()
    {
        static Breadcrumb Crumb(long ms) => new() { TimestampUnixMs = ms };
        // An old event moves forward, carrying a breadcrumb past the end of what an instant holds; a far
        // future one moves back, carrying one past the start.
        var forward = FiledEvent(
            At(Received.AddDays(-45)) with { Breadcrumbs = [Crumb(0), Crumb(DateTimeOffset.MaxValue.ToUnixTimeMilliseconds())] },
            sentAt: null);
        var back = FiledEvent(
            At(Received.AddYears(1)) with { Breadcrumbs = [Crumb(DateTimeOffset.MinValue.ToUnixTimeMilliseconds() + 1)] },
            sentAt: null);

        Assert.Equal(0, forward.Breadcrumbs[0].TimestampUnixMs);
        Assert.Equal(0, forward.Breadcrumbs[1].TimestampUnixMs);
        Assert.Equal(0, back.Breadcrumbs[0].TimestampUnixMs);

        // One the shift lands exactly on the last storable instant is still a time.
        var last = DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();
        var edge = FiledEvent(
            At(Received.AddDays(-45)) with { Breadcrumbs = [Crumb(last - (long)TimeSpan.FromDays(45).TotalMilliseconds)] },
            sentAt: null);
        Assert.Equal(last, edge.Breadcrumbs[0].TimestampUnixMs);
    }
}
