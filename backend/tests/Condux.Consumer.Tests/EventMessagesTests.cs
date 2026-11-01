using System.Text;
using Condux.Consumer;
using Condux.Core.Events;
using Condux.Core.Messaging;
using Confluent.Kafka;
using Xunit;

namespace Condux.Consumer.Tests;

/// <summary>
/// The consumer files an event under its own time bounded to when the relay published its message,
/// read from the message's timestamp, never from the consumer's own clock, so a backlog cannot move it.
/// </summary>
public class EventMessagesTests
{
    private static readonly DateTimeOffset Received = DateTimeOffset.FromUnixTimeSeconds(1790985600);

    private static Message<string, string> MessageAt(
        DateTimeOffset received, int? retentionDays = 30, string? sentAt = null)
    {
        var headers = new Headers();
        if (retentionDays is { } days)
        {
            headers.Add(EventHeaders.RetentionDays, Encoding.UTF8.GetBytes(days.ToString()));
        }
        if (sentAt is not null)
        {
            headers.Add(EventHeaders.SentAt, Encoding.UTF8.GetBytes(sentAt));
        }
        return new Message<string, string> { Key = "7", Value = "{}", Headers = headers, Timestamp = new Timestamp(received) };
    }

    private static DateTimeOffset Filed(Event e, Message<string, string> message) =>
        DateTimeOffset.FromUnixTimeMilliseconds(EventMessages.ToFiledEvent(e, message).TimestampUnixMs);

    [Fact]
    public void Keeps_an_event_time_inside_the_window_of_its_message()
    {
        var e = new Event { TimestampUnixMs = Received.AddHours(-2).ToUnixTimeMilliseconds() };

        Assert.Equal(Received.AddHours(-2), Filed(e, MessageAt(Received)));
    }

    /// <summary>The window is the message's, not the consumer's clock, which may be days later.</summary>
    [Fact]
    public void Files_a_far_future_time_at_the_message_receipt()
    {
        var e = new Event { TimestampUnixMs = new DateTimeOffset(2500, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds() };

        Assert.Equal(Received, Filed(e, MessageAt(Received)));
    }

    /// <summary>The sender's sent_at rides as a header; a malformed one reads as not said.</summary>
    [Fact]
    public void Corrects_the_sender_clock_drift_the_relay_carried()
    {
        var e = new Event { TimestampUnixMs = Received.AddHours(-2).ToUnixTimeMilliseconds() };
        var sentAt = Received.AddHours(-1).ToUnixTimeMilliseconds().ToString();

        Assert.Equal(Received.AddHours(-1), Filed(e, MessageAt(Received, sentAt: sentAt)));
        Assert.Equal(Received.AddHours(-2), Filed(e, MessageAt(Received, sentAt: "an hour ago")));
    }

    [Fact]
    public void Bounds_the_past_by_the_retention_the_relay_stamped()
    {
        var e = new Event { TimestampUnixMs = Received.AddDays(-45).ToUnixTimeMilliseconds() };

        Assert.Equal(Received, Filed(e, MessageAt(Received, 30)));
        Assert.Equal(Received.AddDays(-45), Filed(e, MessageAt(Received, 90)));
    }

    [Fact]
    public void Reads_a_message_with_no_timestamp_as_now()
    {
        var before = DateTimeOffset.UtcNow;

        var read = EventMessages.ToReceivedAt(new Timestamp(0, TimestampType.NotAvailable));

        Assert.InRange(read, before, DateTimeOffset.UtcNow);
    }
}
