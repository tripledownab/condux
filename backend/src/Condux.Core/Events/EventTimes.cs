namespace Condux.Core.Events;

/// <summary>
/// The time an event is filed under. The sender names an event's timestamp, and before this the consumer
/// took it as given, which let anyone holding a DSN (browser SDKs publish theirs) file counts under any
/// hour: a time past 2106 wraps ClickHouse's 32-bit DateTime into an arbitrary earlier one, and any far
/// future or past time moves an issue's first and last seen, its charts, the fix verification count
/// and the dependency inventory's last seen.
///
/// The bounds are the ones Sentry's ingest applies, reimplemented: a timestamp more than a minute ahead
/// of when the event was received, or older than the project's retention, is filed at the time it was
/// received, and so is one that is absent (Sentry's relay, normalize_timestamps in
/// relay-event-normalization, with max_secs_in_future defaulting to 60 and the past bound set to the
/// retention). That is only part of Sentry's handling: before the bounds, Sentry also shifts an event by
/// the envelope's sent_at when the sender's clock is off by 55 minutes or more, and whenever it corrects
/// an event's time, by either rule, it shifts the event's other times (breadcrumbs) by the same amount.
/// Neither is done here: only the event's own time is filed differently.
/// </summary>
public static class EventTimes
{
    /// <summary>How far ahead of its receipt an event may claim to have happened.</summary>
    public static readonly TimeSpan MaxAhead = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The time to file an event under: its own <paramref name="eventUnixMs"/> when that lies between
    /// <paramref name="retentionDays"/> before <paramref name="receivedAt"/> and <see cref="MaxAhead"/>
    /// after it, inclusive, and <paramref name="receivedAt"/> otherwise, including when it is absent (0).
    /// </summary>
    public static DateTimeOffset ToFiledTime(long eventUnixMs, DateTimeOffset receivedAt, int retentionDays)
    {
        // Compared as numbers, because a claimed time may lie outside what DateTimeOffset can hold, and
        // the retention as plain arithmetic, because it comes from a message header and TimeSpan throws
        // on a day count beyond its range. Any int day count fits a long of milliseconds.
        var received = receivedAt.ToUnixTimeMilliseconds();
        var earliest = received - (retentionDays * 86_400_000L);
        var latest = received + (long)MaxAhead.TotalMilliseconds;
        return eventUnixMs > 0 && eventUnixMs >= earliest && eventUnixMs <= latest
            ? DateTimeOffset.FromUnixTimeMilliseconds(eventUnixMs)
            : receivedAt;
    }
}
