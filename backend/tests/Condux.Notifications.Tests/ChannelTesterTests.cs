using System.Net;
using System.Text.Json;
using Condux.Core.Alerting;
using Condux.Notifications;
using Xunit;

namespace Condux.Notifications.Tests;

public class ChannelTesterTests
{
    // Captures the one outbound request and returns 200, so the test copy on the wire can be asserted.
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class ErrorHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    [Fact]
    public async Task Webhook_DeliversFixedTestMessage()
    {
        var handler = new CapturingHandler();
        var tester = new ChannelTester([new WebhookNotifier(new StubHttpClientFactory(handler))]);

        var result = await tester.SendTestAsync(NotificationChannel.Webhook, "https://hooks.test/w");

        Assert.True(result.Delivered);
        Assert.Null(result.Error);
        using var doc = JsonDocument.Parse(handler.Body!);
        Assert.Equal("Condux: test notification", doc.RootElement.GetProperty("subject").GetString());
        Assert.Contains("test notification", doc.RootElement.GetProperty("body").GetString());
    }

    [Fact]
    public async Task Slack_DeliversTestText()
    {
        var handler = new CapturingHandler();
        var tester = new ChannelTester([new SlackNotifier(new StubHttpClientFactory(handler))]);

        var result = await tester.SendTestAsync(NotificationChannel.Slack, "https://hooks.slack.test/s");

        Assert.True(result.Delivered);
        using var doc = JsonDocument.Parse(handler.Body!);
        Assert.Contains("Condux: test notification", doc.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public async Task UnconfiguredChannel_ReportsNotConfigured()
    {
        // Only webhook is registered (email is opt-in on CONDUX_SMTP_HOST), so an email test has no notifier.
        var tester = new ChannelTester([new WebhookNotifier(new StubHttpClientFactory(new CapturingHandler()))]);

        var result = await tester.SendTestAsync(NotificationChannel.Email, "ops@acme.test");

        Assert.False(result.Delivered);
        Assert.Equal("channel_not_configured", result.Error);
    }

    [Fact]
    public async Task DeliveryFailure_IsSurfacedNotThrown()
    {
        var tester = new ChannelTester([new WebhookNotifier(new StubHttpClientFactory(new ErrorHandler()))]);

        var result = await tester.SendTestAsync(NotificationChannel.Webhook, "https://hooks.test/w");

        Assert.False(result.Delivered);
        Assert.False(string.IsNullOrEmpty(result.Error));
    }

    [Fact]
    public async Task AlertTest_RendersTheChannelsTemplate()
    {
        var handler = new CapturingHandler();
        var tester = new ChannelTester([new WebhookNotifier(new StubHttpClientFactory(handler))]);
        var channel = new AlertChannel(
            Guid.NewGuid(), Guid.NewGuid(), NotificationChannel.Webhook, "https://hooks.test/w",
            Template: "{{level}}: {{title}}");

        var result = await tester.SendAlertTestAsync(channel, "acme");

        Assert.True(result.Delivered);
        using var doc = JsonDocument.Parse(handler.Body!);
        // The webhook carries the rendered message from the channel's own template, not the fixed test copy.
        Assert.Equal("Error: Test alert from Condux", doc.RootElement.GetProperty("message").GetString());
    }
}
