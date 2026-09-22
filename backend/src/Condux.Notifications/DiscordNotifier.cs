using System.Net.Http.Json;
using Condux.Core.Alerting;

namespace Condux.Notifications;

/// <summary>Delivers an alert to a Discord incoming-webhook URL (the channel target) as the simple
/// <c>{ "content": ... }</c> payload Discord renders (Discord ignores the generic machine-readable webhook
/// shape, so it gets its own notifier). Clients come from the factory (one per send).</summary>
public sealed class DiscordNotifier(IHttpClientFactory httpClientFactory) : INotifier
{
    public const string ClientName = "discord-notifier";

    public NotificationChannel Channel => NotificationChannel.Discord;

    public async Task SendAsync(
        AlertChannel target, AlertNotification notification, CancellationToken cancellationToken = default)
    {
        var client = httpClientFactory.CreateClient(ClientName);
        // What keeps error text from notifying anybody is allowed_mentions, which governs the mention
        // forms whatever the content says, so the values are substituted as written.
        var content = AlertText.Message(notification, target.Template, AlertTemplate.NoEscape);
        using var response = await client.PostAsJsonAsync(
            target.Target, ToPayload(content), cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task SendMessageAsync(
        string target, string subject, string body, CancellationToken cancellationToken = default)
    {
        var client = httpClientFactory.CreateClient(ClientName);
        using var response = await client.PostAsJsonAsync(
            target, ToPayload($"**{subject}**\n{body}"), cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>The body of every message this notifier sends, so a send path cannot be added without the
    /// mention guard. Both callers are pinned by NotifierTests.
    ///
    /// Suppressing mentions is not only about error text. An org notification names the org, and an org's
    /// name is whatever somebody typed, so that path carries text we did not write just as an alert does.
    /// It went without the guard for as long as it had its own copy of this body.</summary>
    private static object ToPayload(string content) =>
        new { content, allowed_mentions = new { parse = Array.Empty<string>() } };
}
