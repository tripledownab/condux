using Condux.Core.Alerting;
using Condux.Core.Events;

namespace Condux.Notifications;

/// <summary>The outcome of a test send: whether it was delivered, and a short reason when it was not
/// (<c>channel_not_configured</c> for a channel with no registered notifier, else the delivery error).</summary>
public sealed record TestSendResult(bool Delivered, string? Error);

/// <summary>Sends a fixed test message to a single channel target via the <see cref="INotifier"/> for that
/// channel, and reports the outcome instead of throwing. A test send is a feature (unlike best-effort alert
/// dispatch, which swallows), so a delivery failure or an unconfigured channel is surfaced to the caller.</summary>
public sealed class ChannelTester(IEnumerable<INotifier> notifiers)
{
    private readonly IReadOnlyDictionary<NotificationChannel, INotifier> _notifiers =
        notifiers.ToDictionary(notifier => notifier.Channel);

    public async Task<TestSendResult> SendTestAsync(
        NotificationChannel channel, string target, CancellationToken cancellationToken = default)
    {
        // No notifier means the channel is not wired up in this process, e.g. Email without CONDUX_SMTP_HOST.
        if (!_notifiers.TryGetValue(channel, out var notifier))
        {
            return new TestSendResult(false, "channel_not_configured");
        }

        try
        {
            await notifier.SendMessageAsync(
                target, TestNotificationText.Subject, TestNotificationText.Body, cancellationToken);
            return new TestSendResult(true, null);
        }
        catch (Exception ex)
        {
            return new TestSendResult(false, ex.Message);
        }
    }

    /// <summary>Send a test through a rule's channel using its own message template (or the default), so the
    /// test doubles as a template preview: it renders a sample alert exactly as a real one would, rather
    /// than the fixed test copy.</summary>
    public async Task<TestSendResult> SendAlertTestAsync(
        AlertChannel channel, string projectName, CancellationToken cancellationToken = default)
    {
        if (!_notifiers.TryGetValue(channel.Channel, out var notifier))
        {
            return new TestSendResult(false, "channel_not_configured");
        }

        var sample = new AlertNotification(
            0, TestNotificationText.SampleIssueId, "Test alert from Condux",
            "your-app/example.cs:42", Level.Error, AlertEventType.NewIssue, projectName);
        try
        {
            await notifier.SendAsync(channel, sample, cancellationToken);
            return new TestSendResult(true, null);
        }
        catch (Exception ex)
        {
            return new TestSendResult(false, ex.Message);
        }
    }
}

/// <summary>The fixed copy for a test send. Pure, house style (no em-dashes).</summary>
internal static class TestNotificationText
{
    public const string Subject = "Condux: test notification";

    public const string Body =
        "This is a test notification from Condux to confirm this channel is set up correctly. "
        + "If you can read this, delivery works. No action is needed.";

    // A stable, obviously-sample issue id for the alert a channel test renders (see SendAlertTestAsync).
    public static readonly Guid SampleIssueId = Guid.Parse("01920000-0000-7000-8000-000000000000");
}
