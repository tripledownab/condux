namespace Condux.Core.WeeklySummaries;

/// <summary>
/// The pure weekly-send schedule (ADR-0031). Given "now" and an org's (day-of-week, hour, IANA timezone),
/// it computes the most recent scheduled send instant at or before now. The worker wakes hourly and, on the
/// first tick at or after that instant, claims the week in a ledger and sends — so timing is hour-granular,
/// a restart mid-week still catches up, and there is no wall-clock state. Deterministic and unit-tested,
/// including the daylight-saving edges.
/// </summary>
public static class WeeklySchedule
{
    /// <summary>Resolve an IANA zone id, falling back to UTC for an unknown or malformed id (so a bad value
    /// stored on an org degrades to a UTC send rather than breaking the worker).</summary>
    public static TimeZoneInfo ResolveZone(string? timezone) =>
        TryResolveZone(timezone, out var zone) ? zone : TimeZoneInfo.Utc;

    /// <summary>Whether an IANA id resolves to a real zone — the check the settings endpoint applies to user
    /// input, since <see cref="ResolveZone"/> silently falls back to UTC (wrong for validating a value).</summary>
    public static bool IsKnownZone(string? timezone) => TryResolveZone(timezone, out _);

    private static bool TryResolveZone(string? timezone, out TimeZoneInfo zone)
    {
        zone = TimeZoneInfo.Utc;
        if (string.IsNullOrWhiteSpace(timezone))
        {
            return false;
        }

        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(timezone.Trim());
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }

    /// <summary>The most recent scheduled send instant at or before <paramref name="nowUtc"/> for a weekly
    /// send at <paramref name="dayOfWeek"/> (.NET <see cref="DayOfWeek"/>: 0=Sun..6=Sat) and local
    /// <paramref name="hour"/> (0..23) in the org's <paramref name="timezone"/>.</summary>
    public static DateTimeOffset MostRecentSendInstant(
        DateTimeOffset nowUtc, int dayOfWeek, int hour, string? timezone)
    {
        var zone = ResolveZone(timezone);
        hour = Math.Clamp(hour, 0, 23);
        var targetDow = (DayOfWeek)(((dayOfWeek % 7) + 7) % 7);

        // Work in the org's wall clock so the send lands at the local hour regardless of UTC offset / DST.
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(nowUtc.UtcDateTime, zone);
        var daysSinceTarget = ((int)localNow.DayOfWeek - (int)targetDow + 7) % 7;
        var candidate = localNow.Date.AddDays(-daysSinceTarget).AddHours(hour);
        if (candidate > localNow)
        {
            candidate = candidate.AddDays(-7); // the target hour has not arrived yet this week
        }

        // A spring-forward gap makes the wall-clock time non-existent; nudge past it so the UTC conversion is valid.
        if (zone.IsInvalidTime(candidate))
        {
            candidate = candidate.AddHours(1);
        }

        var utc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(candidate, DateTimeKind.Unspecified), zone);
        return new DateTimeOffset(utc, TimeSpan.Zero);
    }

    /// <summary>The scheduled send instant if it is **due now** — at or before <paramref name="nowUtc"/> and
    /// within <paramref name="maxDelay"/> of it — else null. This bounds catch-up: a first tick right after a
    /// deploy, or an org enabling the digest mid-week, must NOT fire the stale previous week; it waits for the
    /// next scheduled send. <paramref name="maxDelay"/> must exceed the worker's tick interval so a normal
    /// tick landing shortly after the scheduled hour still qualifies.</summary>
    public static DateTimeOffset? DueSendInstant(
        DateTimeOffset nowUtc, int dayOfWeek, int hour, string? timezone, TimeSpan maxDelay)
    {
        var sendInstant = MostRecentSendInstant(nowUtc, dayOfWeek, hour, timezone);
        return nowUtc - sendInstant <= maxDelay ? sendInstant : null;
    }
}
