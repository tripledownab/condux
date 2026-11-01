namespace Condux.Core.Events;

/// <summary>
/// The time an event is filed under. The sender names an event's timestamp, and before this the consumer
/// took it as given, which let anyone holding a DSN (browser SDKs publish theirs) file counts under any
/// hour: a time past 2106 wraps ClickHouse's 32-bit DateTime into an arbitrary earlier one, and any far
/// future or past time moves an issue's first and last seen, its charts, the fix verification count
/// and the dependency inventory's last seen.
///
/// This is Sentry's ingest handling, reimplemented from relay at commit 9e49155. First, when an envelope
/// says when it was sent and that differs from its receipt by 55 minutes or more, the sender's clock is
/// taken to be off by the difference and the event is shifted by it (MINIMUM_CLOCK_DRIFT in
/// relay-server/src/services/processor.rs, ClockDriftProcessor in
/// relay-event-normalization/src/clock_drift.rs). Then the bounds: a timestamp more than a minute ahead
/// of receipt, older than the project's retention, or absent is filed at receipt (normalize_timestamps
/// in relay-event-normalization/src/validation.rs, with max_secs_in_future defaulting to 60 and the
/// past bound set to the retention). Whatever the event's time moved by, its breadcrumbs move by too, as
/// Sentry shifts every timestamp in the event by each correction, so their order around the error holds.
/// </summary>
public static class EventTimes
{
    /// <summary>How far ahead of its receipt an event may claim to have happened.</summary>
    public static readonly TimeSpan MaxAhead = TimeSpan.FromMinutes(1);

    /// <summary>The smallest gap between an envelope's sent_at and its receipt read as the sender's clock
    /// being off rather than as network delay.</summary>
    public static readonly TimeSpan MinClockDrift = TimeSpan.FromMinutes(55);

    // The widest instant everything downstream can represent, read from the type that decides it rather
    // than written out, so the two cannot drift. It is narrower than both ClickHouse DateTime64 and the
    // ECMAScript Date range the dashboard parses the value back into, so bounding here bounds those. A
    // sender names every time an event carries, so a value outside the range is not a clock reading, and
    // one that reaches storage costs more than the event it came with: every reader that turns it back
    // into an instant throws on it, and the consumer's drain loop pauses before its next message. The
    // parser bounds what arrives by it, and a shifted breadcrumb is bounded by it again here.
    internal static readonly long MinStorableMs = DateTimeOffset.MinValue.ToUnixTimeMilliseconds();
    internal static readonly long MaxStorableMs = DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();

    /// <summary>
    /// The event as it is filed: shifted by the sender's clock drift when <paramref name="sentAtUnixMs"/>
    /// shows one (0 means the envelope did not say), then bounded by <see cref="ToFiledTime"/>, with every
    /// breadcrumb moved by the same amount as the event.
    /// </summary>
    public static Event ToFiledEvent(Event e, DateTimeOffset receivedAt, int retentionDays, long sentAtUnixMs)
    {
        var received = receivedAt.ToUnixTimeMilliseconds();
        var drift = sentAtUnixMs != 0 && Math.Abs(received - sentAtUnixMs) >= (long)MinClockDrift.TotalMilliseconds
            ? received - sentAtUnixMs
            : 0;
        // 0 is the parser's only spelling of absent. A negative time is a time, if an implausible one, and
        // the bounds file it at receipt as a correction like any other.
        var claimed = e.TimestampUnixMs;
        var filed = ToFiledTime(claimed != 0 ? claimed + drift : 0, receivedAt, retentionDays).ToUnixTimeMilliseconds();
        // An absent time is filed at receipt without being a correction, so only the drift moves the rest.
        var shift = claimed != 0 ? filed - claimed : drift;
        return e with
        {
            TimestampUnixMs = filed,
            Breadcrumbs = shift == 0 ? e.Breadcrumbs : [.. e.Breadcrumbs.Select(b => Shift(b, shift))],
        };
    }

    // An absent breadcrumb time stays absent, and one the shift carries outside the storable range becomes
    // absent too, as the parser makes one that arrives outside it.
    private static Breadcrumb Shift(Breadcrumb b, long shift)
    {
        if (b.TimestampUnixMs == 0)
        {
            return b;
        }
        var moved = b.TimestampUnixMs + shift;
        return b with { TimestampUnixMs = moved >= MinStorableMs && moved <= MaxStorableMs ? moved : 0 };
    }

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
