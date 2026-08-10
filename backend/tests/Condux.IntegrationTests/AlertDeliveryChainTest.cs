extern alias consumer;

using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Condux.Core.Alerting;
using Condux.Core.Events;
using Condux.Core.Grouping;
using Condux.IntegrationTests.Fixtures;
using Condux.Notifications;
using Condux.Storage.Postgres;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using AlertNotificationFactory = consumer::Condux.Consumer.AlertNotificationFactory;

namespace Condux.IntegrationTests;

/// <summary>
/// The whole alert path end to end, no fakes: an <see cref="UpsertResult"/> (as the consumer produces for a
/// new issue) becomes an <c>AlertNotification</c> via the consumer's <c>AlertNotificationFactory</c>, is
/// dispatched through a real <see cref="AlertDispatcher"/> that loads a seeded rule + webhook channel from
/// Postgres, and is delivered by the real <see cref="WebhookNotifier"/> over a real socket to an in-process
/// HttpListener. Proves rule-load -> match -> real HTTP delivery, which the per-piece tests do not.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AlertDeliveryChainTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private sealed class RealHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    [Fact]
    public async Task New_error_issue_is_delivered_to_a_real_webhook_endpoint()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);

        // A free loopback port for the receiver, then the org/project/rule/channel that route to it.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var url = $"http://127.0.0.1:{port}/hook/";

        var org = await new OrgRepository(pg.ConnectionString).CreateAsync(
            "acme-" + Guid.NewGuid().ToString("N"), "Acme", 2);
        var project = await new ProjectRepository(pg.ConnectionString).CreateAsync(org.Id, "Backend", "python");
        var rules = new AlertRuleRepository(pg.ConnectionString);
        var rule = await rules.CreateRuleAsync(
            project.Id, "prod", [AlertEventType.NewIssue, AlertEventType.Regression], [Level.Error, Level.Fatal]);
        await rules.AddChannelAsync(rule.Id, NotificationChannel.Webhook, url);

        // The receiver captures the one POST the notifier sends.
        using var listener = new HttpListener();
        listener.Prefixes.Add(url);
        listener.Start();
        var body = "";
        var serverTask = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            using var reader = new StreamReader(context.Request.InputStream);
            body = await reader.ReadToEndAsync();
            context.Response.StatusCode = 200;
            context.Response.Close();
        });

        // The consumer seam: a brand-new issue (occurrence 1) becomes the alert notification.
        var upsert = new UpsertResult(Id: 1, Occurrence: 1, PublicId: Guid.CreateVersion7(), Reopened: false);
        var grouping = new Grouping("fp-chain", "TypeError: boom", "app.py:42");
        var notification = AlertNotificationFactory.Build(project.Id, upsert, grouping, Level.Error);
        Assert.NotNull(notification);

        // Real dispatcher (loads the seeded rule + channel) + real webhook notifier -> real socket.
        var dispatcher = new AlertDispatcher(
            rules, new ProjectRepository(pg.ConnectionString),
            [new WebhookNotifier(new RealHttpClientFactory())], NullLogger<AlertDispatcher>.Instance);
        await dispatcher.DispatchAsync(project.Id, notification!.EventType, notification);

        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        listener.Stop();

        using var doc = JsonDocument.Parse(body);
        Assert.Equal("new_issue", doc.RootElement.GetProperty("trigger").GetString());
        Assert.Equal("TypeError: boom", doc.RootElement.GetProperty("title").GetString());
        Assert.Equal("app.py:42", doc.RootElement.GetProperty("culprit").GetString());
        Assert.Equal(project.Name, doc.RootElement.GetProperty("project").GetString());
    }
}
