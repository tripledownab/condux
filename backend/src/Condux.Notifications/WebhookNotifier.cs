using System.Net.Http.Json;
using Condux.Core.Alerting;

namespace Condux.Notifications;

/// <summary>Delivers an alert as a JSON POST to an arbitrary webhook URL (the channel target). The body
/// is a stable machine-readable shape so a receiver can route on it. Clients come from the factory (one
/// per send) so this stays safe as the singleton the dispatcher holds.</summary>
public sealed class WebhookNotifier(IHttpClientFactory httpClientFactory) : INotifier
{
    public const string ClientName = "webhook-notifier";

    public NotificationChannel Channel => NotificationChannel.Webhook;

    public async Task SendAsync(
        AlertChannel target, AlertNotification notification, CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            trigger = WebhookEvent(notification.EventType),
            level = notification.Level.ToString(),
            title = notification.Title,
            culprit = notification.Culprit,
            project = notification.ProjectName,
            issueId = notification.IssuePublicId.ToString(),
            // The rendered human message (the channel's template or the default) alongside the structured fields.
            message = AlertText.Message(notification, target.Template),
        };
        var client = httpClientFactory.CreateClient(ClientName);
        using var response = await client.PostAsJsonAsync(target.Target, payload, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task SendMessageAsync(
        string target, string subject, string body, CancellationToken cancellationToken = default)
    {
        var client = httpClientFactory.CreateClient(ClientName);
        using var response = await client.PostAsJsonAsync(
            target, new { subject, body }, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    // Stable machine-readable event name for the webhook body (a receiver routes on it).
    private static string WebhookEvent(AlertEventType eventType) => eventType switch
    {
        AlertEventType.NewIssue => "new_issue",
        AlertEventType.Regression => "regression",
        AlertEventType.Resolved => "resolved",
        AlertEventType.Assigned => "assigned",
        _ => "alert",
    };
}
