using System.Text.Json;
using Condux.Core.Alerting;
using Condux.Core.Events;
using Condux.Tests.Shared;
using Xunit;

namespace Condux.Notifications.Tests;

/// <summary>Proves the HTTP notifiers (webhook + Slack) actually deliver over a REAL socket end to end: an
/// in-process HttpListener receives the POST each notifier sends via a real HttpClient. The stub-handler
/// tests can't catch a real transport/HttpClient regression. Unit-category (no Docker); random loopback
/// port, so it never collides with a running compose stack.</summary>
public class RealSocketDeliveryTest
{
    private sealed class RealHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private static readonly AlertNotification Notification = new(
        7, Guid.NewGuid(), "TypeError: boom", "app.py", Level.Error, AlertEventType.NewIssue);

    private static AlertChannel Channel(NotificationChannel channel, string url) =>
        new(Guid.NewGuid(), Guid.NewGuid(), channel, url);

    // Run send(url) against a real loopback receiver and return the request it captured.
    private static async Task<CapturedRequest> CaptureAsync(Func<string, Task> send)
    {
        using var receiver = LoopbackHttpReceiver.Start();
        var next = receiver.NextRequestAsync();
        await send(receiver.Url);
        return await next.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Webhook_delivers_the_json_payload_over_a_real_socket()
    {
        var (method, contentType, body) = await CaptureAsync(url =>
            new WebhookNotifier(new RealHttpClientFactory())
                .SendAsync(Channel(NotificationChannel.Webhook, url), Notification));

        Assert.Equal("POST", method);
        Assert.Contains("application/json", contentType);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("new_issue", doc.RootElement.GetProperty("trigger").GetString());
        Assert.Equal("TypeError: boom", doc.RootElement.GetProperty("title").GetString());
        Assert.Equal("app.py", doc.RootElement.GetProperty("culprit").GetString());
    }

    [Fact]
    public async Task Slack_delivers_the_text_payload_over_a_real_socket()
    {
        var (method, _, body) = await CaptureAsync(url =>
            new SlackNotifier(new RealHttpClientFactory())
                .SendAsync(Channel(NotificationChannel.Slack, url), Notification));

        Assert.Equal("POST", method);
        using var doc = JsonDocument.Parse(body);
        var text = doc.RootElement.GetProperty("text").GetString();
        Assert.Contains("New issue", text);
        Assert.Contains("TypeError: boom", text);
    }
}
