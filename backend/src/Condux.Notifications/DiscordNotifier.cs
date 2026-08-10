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
        using var response = await client.PostAsJsonAsync(
            target.Target,
            new
            {
                content = AlertText.Message(notification, target.Template),
                // Never let error-derived text ping a channel; suppress @everyone/@here/role mentions.
                allowed_mentions = new { parse = Array.Empty<string>() },
            },
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task SendMessageAsync(
        string target, string subject, string body, CancellationToken cancellationToken = default)
    {
        var client = httpClientFactory.CreateClient(ClientName);
        using var response = await client.PostAsJsonAsync(
            target, new { content = $"**{subject}**\n{body}" }, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
