using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Condux.Core.Alerting;

namespace Condux.Notifications;

/// <summary>Delivers an alert to a Slack incoming-webhook URL (the channel target) as the simple
/// <c>{ "text": ... }</c> payload Slack expects. Clients come from the factory (one per send).</summary>
public sealed partial class SlackNotifier(IHttpClientFactory httpClientFactory) : INotifier
{
    public const string ClientName = "slack-notifier";

    public NotificationChannel Channel => NotificationChannel.Slack;

    public async Task SendAsync(
        AlertChannel target, AlertNotification notification, CancellationToken cancellationToken = default)
    {
        var client = httpClientFactory.CreateClient(ClientName);
        var text = NeutralizeBroadcasts(AlertText.Message(notification, target.Template));
        using var response = await client.PostAsJsonAsync(target.Target, new { text }, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task SendMessageAsync(
        string target, string subject, string body, CancellationToken cancellationToken = default)
    {
        var client = httpClientFactory.CreateClient(ClientName);
        var text = NeutralizeBroadcasts($"*{subject}*\n{body}");
        using var response = await client.PostAsJsonAsync(target, new { text }, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    // Slack renders <!channel>/<!here>/<!everyone>, <@user> and <!subteam^id> in the text field as live
    // notifications, and a webhook's text has no allowed_mentions flag (Discord's guard). A template value
    // can be untrusted error text, so escape the opening < of any such entity to &lt; — Slack then shows it
    // literally and pings no one. A genuine <https://url|label> link (the < is followed by 'h', not !/@) is
    // left intact. Verified against docs.slack.dev/messaging/formatting-message-text.
    private static string NeutralizeBroadcasts(string text) => BroadcastEntity().Replace(text, "&lt;");

    [GeneratedRegex("<(?=[!@])")]
    private static partial Regex BroadcastEntity();
}
