using System.Net.Http.Json;
using Condux.Core.Alerting;

namespace Condux.Notifications;

/// <summary>Delivers an alert to a Slack incoming-webhook URL (the channel target) as the simple
/// <c>{ "text": ... }</c> payload Slack expects. Clients come from the factory (one per send).</summary>
public sealed class SlackNotifier(IHttpClientFactory httpClientFactory) : INotifier
{
    public const string ClientName = "slack-notifier";

    public NotificationChannel Channel => NotificationChannel.Slack;

    public async Task SendAsync(
        AlertChannel target, AlertNotification notification, CancellationToken cancellationToken = default)
    {
        var client = httpClientFactory.CreateClient(ClientName);
        var text = AlertText.Message(notification, target.Template, Escape);
        using var response = await client.PostAsJsonAsync(target.Target, new { text }, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task SendMessageAsync(
        string target, string subject, string body, CancellationToken cancellationToken = default)
    {
        var client = httpClientFactory.CreateClient(ClientName);
        var text = $"*{Escape(subject)}*\n{Escape(body)}";
        using var response = await client.PostAsJsonAsync(target, new { text }, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    // Slack treats &, < and > as the control characters of a message's text and decodes exactly those
    // three HTML entities back when it displays one, so replacing them is how text is shown as written
    // instead of parsed. That is what stops an error title becoming <!channel>, which pings a whole
    // workspace and which an incoming webhook has no flag to suppress, or <https://elsewhere|Open the
    // issue>, which is a link whose visible label can claim anything. The ampersand is replaced first or
    // the entities produced by the other two are rewritten in turn.
    //
    // Only a substituted value gets this. The template around it is an admin's and may hold markup they
    // meant, and the bold marks above are ours, so encoding the finished string would break both.
    private static string Escape(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
