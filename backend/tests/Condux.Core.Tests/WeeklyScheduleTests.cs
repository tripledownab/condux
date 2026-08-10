using Condux.Core.WeeklySummaries;
using Xunit;

namespace Condux.Core.Tests;

public class WeeklyScheduleTests
{
    // 2024-01-01 is a Monday; the surrounding assertions anchor on that known weekday.
    private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute = 0) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public void ResolveZone_UnknownOrEmpty_FallsBackToUtc()
    {
        Assert.Equal(TimeZoneInfo.Utc, WeeklySchedule.ResolveZone(null));
        Assert.Equal(TimeZoneInfo.Utc, WeeklySchedule.ResolveZone(""));
        Assert.Equal(TimeZoneInfo.Utc, WeeklySchedule.ResolveZone("Not/AZone"));
    }

    [Fact]
    public void MostRecentSendInstant_Utc_ReturnsThisWeeksSendWhenPast()
    {
        // Wednesday noon: the most recent Monday 09:00 send is earlier the same week.
        var now = Utc(2024, 1, 3, 12);
        var instant = WeeklySchedule.MostRecentSendInstant(now, (int)DayOfWeek.Monday, 9, "UTC");
        Assert.Equal(Utc(2024, 1, 1, 9), instant);
    }

    [Fact]
    public void MostRecentSendInstant_BeforeTheHour_RollsBackToPreviousWeek()
    {
        // Monday 08:00, before the 09:00 send: the last send was the previous Monday.
        var now = Utc(2024, 1, 1, 8);
        var instant = WeeklySchedule.MostRecentSendInstant(now, (int)DayOfWeek.Monday, 9, "UTC");
        Assert.Equal(Utc(2023, 12, 25, 9), instant);
    }

    [Fact]
    public void MostRecentSendInstant_ExactlyOnTheHour_IsIncluded()
    {
        var now = Utc(2024, 1, 1, 9);
        var instant = WeeklySchedule.MostRecentSendInstant(now, (int)DayOfWeek.Monday, 9, "UTC");
        Assert.Equal(Utc(2024, 1, 1, 9), instant);
    }

    [Fact]
    public void MostRecentSendInstant_HonorsTheOrgTimezone()
    {
        // Monday 09:00 in New York (EST, UTC-5 in January) is 14:00 UTC. Evaluated Wednesday.
        var now = Utc(2024, 1, 3, 12);
        var instant = WeeklySchedule.MostRecentSendInstant(now, (int)DayOfWeek.Monday, 9, "America/New_York");
        Assert.Equal(Utc(2024, 1, 1, 14), instant);
    }

    [Fact]
    public void MostRecentSendInstant_SucceedingWeeksAreSevenDaysApart()
    {
        var week1 = WeeklySchedule.MostRecentSendInstant(Utc(2024, 1, 1, 10), (int)DayOfWeek.Monday, 9, "UTC");
        var week2 = WeeklySchedule.MostRecentSendInstant(Utc(2024, 1, 8, 10), (int)DayOfWeek.Monday, 9, "UTC");
        Assert.Equal(TimeSpan.FromDays(7), week2 - week1);
    }

    [Fact]
    public void MostRecentSendInstant_SpringForwardGap_DoesNotThrowAndStaysInThePast()
    {
        // A send scheduled for 02:00 on the US spring-forward Sunday (2024-03-10) lands in the non-existent
        // hour. The schedule must nudge past the gap rather than throw, and never return a future instant.
        var now = Utc(2024, 3, 11, 12);
        var instant = WeeklySchedule.MostRecentSendInstant(now, (int)DayOfWeek.Sunday, 2, "America/New_York");
        Assert.True(instant <= now);
        Assert.True(instant > now.AddDays(-8));
    }

    [Fact]
    public void MostRecentSendInstant_ClampsOutOfRangeInputs()
    {
        // A stored hour/day out of range must not throw; hour clamps to [0,23] and day wraps mod 7.
        var now = Utc(2024, 1, 3, 12);
        var instant = WeeklySchedule.MostRecentSendInstant(now, dayOfWeek: 8, hour: 30, "UTC");
        Assert.True(instant <= now);
    }

    [Fact]
    public void DueSendInstant_ReturnsTheInstant_WhenRecentlyScheduled()
    {
        var now = Utc(2024, 1, 1, 10); // 1h after Monday 09:00
        var instant = WeeklySchedule.DueSendInstant(now, (int)DayOfWeek.Monday, 9, "UTC", TimeSpan.FromHours(6));
        Assert.Equal(Utc(2024, 1, 1, 9), instant);
    }

    [Fact]
    public void DueSendInstant_ReturnsNull_WhenTheLastSendIsStale()
    {
        // Wednesday noon: the last Monday 09:00 send is ~51h old — a deploy or mid-week enable must not fire it.
        var now = Utc(2024, 1, 3, 12);
        Assert.Null(WeeklySchedule.DueSendInstant(now, (int)DayOfWeek.Monday, 9, "UTC", TimeSpan.FromHours(6)));
    }

    [Fact]
    public void DueSendInstant_IsInclusiveAtTheDelayBoundary()
    {
        var now = Utc(2024, 1, 1, 15); // exactly 6h after Monday 09:00
        Assert.NotNull(WeeklySchedule.DueSendInstant(now, (int)DayOfWeek.Monday, 9, "UTC", TimeSpan.FromHours(6)));
    }
}
