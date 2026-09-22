using System.Net;
using System.Text.Json;
using Condux.Core.Alerting;
using Condux.Notifications;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Condux.Notifications.Tests;

public class ChannelTesterTests
{
    // Captures the one outbound request and returns 200, so the test copy on the wire can be asserted.
    // Calls is counted separately from Body so a test can tell "nothing was sent" from "sent no content".
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }

        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
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

    private static ChannelTester Tester(params INotifier[] notifiers) =>
        new(notifiers, NullLogger<ChannelTester>.Instance);

    [Fact]
    public async Task Webhook_DeliversFixedTestMessage()
    {
        var handler = new CapturingHandler();
        var tester = Tester([new WebhookNotifier(new StubHttpClientFactory(handler))]);

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
        var tester = Tester([new SlackNotifier(new StubHttpClientFactory(handler))]);

        var result = await tester.SendTestAsync(NotificationChannel.Slack, "https://hooks.slack.test/s");

        Assert.True(result.Delivered);
        using var doc = JsonDocument.Parse(handler.Body!);
        Assert.Contains("Condux: test notification", doc.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public async Task UnconfiguredChannel_ReportsNotConfigured()
    {
        // Only webhook is registered (email is opt-in on CONDUX_SMTP_HOST), so an email test has no notifier.
        var tester = Tester([new WebhookNotifier(new StubHttpClientFactory(new CapturingHandler()))]);

        var result = await tester.SendTestAsync(NotificationChannel.Email, "ops@acme.test");

        Assert.False(result.Delivered);
        Assert.Equal("channel_not_configured", result.Error);
    }

    [Fact]
    public async Task DeliveryFailure_IsSurfacedNotThrown()
    {
        var tester = Tester([new WebhookNotifier(new StubHttpClientFactory(new ErrorHandler()))]);

        var result = await tester.SendTestAsync(NotificationChannel.Webhook, "https://hooks.test/w");

        Assert.False(result.Delivered);
        Assert.Equal("delivery_failed", result.Error);
    }

    [Fact]
    public async Task DeliveryFailure_DoesNotReportWhatTheDestinationDid()
    {
        // The handler answers 500, and EnsureSuccessStatusCode puts that code in its exception message.
        // Returning the message would tell the caller what an address they chose actually did, which is
        // the whole of what makes a test send usable for probing. Assert on the absence, because the
        // token assertion above would still pass if the status were appended to it.
        var tester = Tester([new WebhookNotifier(new StubHttpClientFactory(new ErrorHandler()))]);

        var result = await tester.SendTestAsync(NotificationChannel.Webhook, "https://hooks.test/w");

        // NotNull first: DoesNotContain passes against null, so without this the test would also pass if
        // the reason went missing entirely.
        Assert.NotNull(result.Error);
        Assert.DoesNotContain("500", result.Error);
        Assert.DoesNotContain("Internal Server Error", result.Error);
    }

    [Fact]
    public async Task StoredTargetThatIsNotAUrl_IsRefusedWithoutSending()
    {
        // Targets saved before they were checked were never checked, so the value is asked about again
        // here. Calls proves the refusal happened before the request, not after it failed.
        var handler = new CapturingHandler();
        var tester = Tester([new WebhookNotifier(new StubHttpClientFactory(handler))]);

        var result = await tester.SendTestAsync(NotificationChannel.Webhook, "hooks.test/w");

        Assert.False(result.Delivered);
        Assert.Equal("invalid_target", result.Error);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task AlertTest_StoredTargetThatIsNotAUrl_IsRefusedWithoutSending()
    {
        var handler = new CapturingHandler();
        var tester = Tester([new SlackNotifier(new StubHttpClientFactory(handler))]);
        var channel = new AlertChannel(
            Guid.NewGuid(), Guid.NewGuid(), NotificationChannel.Slack, "file:///etc/passwd");

        var result = await tester.SendAlertTestAsync(channel, "acme");

        Assert.False(result.Delivered);
        Assert.Equal("invalid_target", result.Error);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task AlertTest_RendersTheChannelsTemplate()
    {
        var handler = new CapturingHandler();
        var tester = Tester([new WebhookNotifier(new StubHttpClientFactory(handler))]);
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
