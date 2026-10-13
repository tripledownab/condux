using System.Globalization;
using System.Text;
using Condux.Core.Events;
using Condux.Core.Messaging;
using Condux.Core.Plans;
using Confluent.Kafka;

namespace Condux.Consumer;

/// <summary>
/// What a consumed event message says beyond the event itself, and the time the event is filed under,
/// which needs both: the retention the relay stamped, and when the message was received.
/// </summary>
public static class EventMessages
{
    /// <summary>
    /// The event as it is filed: its own time bounded to its receipt and the project's retention
    /// (<see cref="EventTimes"/>), so a sender cannot file it under any hour it likes. The event carries
    /// the bounded time, so the issue, the rollup and the stored row all read the same one.
    /// </summary>
    public static Event ToFiledEvent(Event e, Message<string, string> message) => e with
    {
        TimestampUnixMs = EventTimes
            .ToFiledTime(e.TimestampUnixMs, ToReceivedAt(message.Timestamp), ReadRetentionDays(message.Headers))
            .ToUnixTimeMilliseconds(),
    };

    // The relay stamps the project's plan-tier retention as a Kafka header. Fall back to the single
    // PlanCatalog default when it is absent (an older message) or malformed, so an event is never written
    // with a zero/invalid TTL.
    public static int ReadRetentionDays(Headers headers)
    {
        if (headers.TryGetLastBytes(EventHeaders.RetentionDays, out var bytes)
            && int.TryParse(Encoding.UTF8.GetString(bytes), NumberStyles.None, CultureInfo.InvariantCulture, out var days)
            && days > 0)
        {
            return days;
        }
        return PlanCatalog.DefaultRetentionDays;
    }

    // The instant the broker holds for the message: the relay's producer stamps it when it publishes, or
    // the broker does on append, either way moments after the relay received the event, and neither is
    // anything the sender chose. Consumer lag does not move it. A message carrying none is read as now.
    public static DateTimeOffset ToReceivedAt(Timestamp timestamp) =>
        timestamp.Type == TimestampType.NotAvailable
            ? DateTimeOffset.UtcNow
            : DateTimeOffset.FromUnixTimeMilliseconds(timestamp.UnixTimestampMs);
}
