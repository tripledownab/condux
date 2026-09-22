using Condux.Core.Alerting;
using Condux.Core.Events;
using Microsoft.Extensions.Logging;

namespace Condux.Notifications;

/// <summary>The outcome of a test send: whether it was delivered, and one of the reasons below when it
/// was not. The set is closed, and what it costs to keep it closed is stated on
/// <see cref="ChannelTester"/>.</summary>
public sealed record TestSendResult(bool Delivered, string? Error)
{
    /// <summary>No notifier is registered for the channel in this process, e.g. email with no SMTP relay.</summary>
    public const string NotConfigured = "channel_not_configured";

    /// <summary>The stored target is not a usable target for its channel, so nothing was sent.</summary>
    public const string InvalidTarget = "invalid_target";

    /// <summary>The send was attempted and did not succeed. Why is in the server log, not here.</summary>
    public const string DeliveryFailed = "delivery_failed";
}

/// <summary>Sends a fixed test message to a single channel target via the <see cref="INotifier"/> for that
/// channel, and reports the outcome instead of throwing. A test send is a feature (unlike best-effort alert
/// dispatch, which swallows), so a delivery failure or an unconfigured channel is surfaced to the caller.
///
/// The caller is told WHICH of a fixed set of things happened and never the exception's own text. The
/// target is a URL an org admin chose and the platform then opens from inside the deployment, so a
/// message describing what the connection did carries back whether a host answered, refused or was not
/// there at all. Anyone can sign up and own an org, which makes the pair of endpoints behind this a way
/// to ask that question about an arbitrary address. Reporting the shape of the failure and logging the
/// detail keeps the answer on the server, where an operator can still read it.</summary>
public sealed class ChannelTester(IEnumerable<INotifier> notifiers, ILogger<ChannelTester> logger)
{
    private readonly IReadOnlyDictionary<NotificationChannel, INotifier> _notifiers =
        notifiers.ToDictionary(notifier => notifier.Channel);

    public async Task<TestSendResult> SendTestAsync(
        NotificationChannel channel, string target, CancellationToken cancellationToken = default)
    {
        // No notifier means the channel is not wired up in this process, e.g. Email without CONDUX_SMTP_HOST.
        if (!_notifiers.TryGetValue(channel, out var notifier))
        {
            return new TestSendResult(false, TestSendResult.NotConfigured);
        }

        // Rows written before the target was checked on save were never checked at all, so the value is
        // asked about again here rather than trusted because it is stored.
        if (!ChannelTargets.IsValid(channel, target))
        {
            return new TestSendResult(false, TestSendResult.InvalidTarget);
        }

        try
        {
            await notifier.SendMessageAsync(
                target, TestNotificationText.Subject, TestNotificationText.Body, cancellationToken);
            return new TestSendResult(true, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Test send to a {Channel} channel failed", channel);
            return new TestSendResult(false, TestSendResult.DeliveryFailed);
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
            return new TestSendResult(false, TestSendResult.NotConfigured);
        }

        if (!ChannelTargets.IsValid(channel.Channel, channel.Target))
        {
            return new TestSendResult(false, TestSendResult.InvalidTarget);
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
            logger.LogWarning(ex, "Test alert to a {Channel} channel failed", channel.Channel);
            return new TestSendResult(false, TestSendResult.DeliveryFailed);
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
