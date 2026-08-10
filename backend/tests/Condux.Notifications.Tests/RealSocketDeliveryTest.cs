using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Condux.Core.Alerting;
using Condux.Core.Events;
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

    // Stand up an HttpListener on a free loopback port, run send(url) against it, and return the captured
    // request. HttpListener queues the request, so there is no accept/send race.
    private static async Task<(string Method, string? ContentType, string Body)> CaptureAsync(
        Func<string, Task> send)
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        using var listener = new HttpListener();
        var url = $"http://127.0.0.1:{port}/hook/";
        listener.Prefixes.Add(url);
        listener.Start();

        var method = "";
        string? contentType = null;
        var body = "";
        var serverTask = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            method = context.Request.HttpMethod;
            contentType = context.Request.ContentType;
            using var reader = new StreamReader(context.Request.InputStream);
            body = await reader.ReadToEndAsync();
            context.Response.StatusCode = 200;
            context.Response.Close();
        });

        await send(url);
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        listener.Stop();
        return (method, contentType, body);
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
