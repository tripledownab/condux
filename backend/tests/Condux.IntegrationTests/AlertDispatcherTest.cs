using Condux.Core.Alerting;
using Condux.Core.Events;
using Condux.IntegrationTests.Fixtures;
using Condux.Notifications;
using Condux.Storage.Postgres;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// The alert engine end to end against a real Postgres (#55): the dispatcher loads a project's enabled
/// rules, fires the matching ones, and delivers to their channels through the registered notifier — but
/// only when the issue meets the rule's trigger and severity. A capturing fake notifier stands in for
/// the real email/Slack/webhook ones (#56-58), so no network is touched.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AlertDispatcherTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private sealed class CapturingNotifier(NotificationChannel channel) : INotifier
    {
        public NotificationChannel Channel { get; } = channel;
        public List<(AlertChannel Target, AlertNotification Notification)> Sent { get; } = [];

        public Task SendAsync(
            AlertChannel target, AlertNotification notification, CancellationToken cancellationToken = default)
        {
            Sent.Add((target, notification));
            return Task.CompletedTask;
        }

        public Task SendMessageAsync(
            string target, string subject, string body, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private async Task<long> SeedProjectAsync()
    {
        var org = await new OrgRepository(pg.ConnectionString).CreateAsync(
            "acme-" + Guid.NewGuid().ToString("N"), "Acme", 2);
        var project = await new ProjectRepository(pg.ConnectionString).CreateAsync(
            org.Id, "Backend", "python");
        return project.Id;
    }

    private static AlertNotification NewIssue(long projectId, Level level) =>
        new(projectId, Guid.CreateVersion7(), "TypeError: boom", "app.py", level, AlertEventType.NewIssue);

    [Fact]
    public async Task Dispatch_DeliversToMatchingRulesChannel()
    {
        var repo = new AlertRuleRepository(pg.ConnectionString);
        var projectId = await SeedProjectAsync();
        var rule = await repo.CreateRuleAsync(
            projectId, "prod", [AlertEventType.NewIssue, AlertEventType.Regression], [Level.Error, Level.Fatal]);
        await repo.AddChannelAsync(rule.Id, NotificationChannel.Slack, "https://hooks.slack.test/x");

        var slack = new CapturingNotifier(NotificationChannel.Slack);
        var dispatcher = new AlertDispatcher(
            repo, new ProjectRepository(pg.ConnectionString), [slack], NullLogger<AlertDispatcher>.Instance);

        await dispatcher.DispatchAsync(projectId, AlertEventType.NewIssue, NewIssue(projectId, Level.Error));

        var sent = Assert.Single(slack.Sent);
        Assert.Equal("https://hooks.slack.test/x", sent.Target.Target);
        Assert.Equal("TypeError: boom", sent.Notification.Title);
    }

    [Fact]
    public async Task Dispatch_SkipsWhenLevelNotInRuleSet()
    {
        var repo = new AlertRuleRepository(pg.ConnectionString);
        var projectId = await SeedProjectAsync();
        var rule = await repo.CreateRuleAsync(
            projectId, "fatal-only", [AlertEventType.NewIssue, AlertEventType.Regression], [Level.Fatal]);
        await repo.AddChannelAsync(rule.Id, NotificationChannel.Webhook, "https://webhook.test/x");

        var webhook = new CapturingNotifier(NotificationChannel.Webhook);
        var dispatcher = new AlertDispatcher(
            repo, new ProjectRepository(pg.ConnectionString), [webhook], NullLogger<AlertDispatcher>.Instance);

        await dispatcher.DispatchAsync(projectId, AlertEventType.NewIssue, NewIssue(projectId, Level.Error));

        Assert.Empty(webhook.Sent); // Error is not in the rule's Fatal-only set
    }

    [Fact]
    public async Task Dispatch_DedupesDeliveryToTheSameDestinationAcrossRules()
    {
        var repo = new AlertRuleRepository(pg.ConnectionString);
        var projectId = await SeedProjectAsync();

        // Two enabled rules both fire on an Error and both deliver to the SAME webhook target.
        var ruleA = await repo.CreateRuleAsync(
            projectId, "a", [AlertEventType.NewIssue, AlertEventType.Regression], [Level.Error]);
        await repo.AddChannelAsync(ruleA.Id, NotificationChannel.Webhook, "https://webhook.test/same");
        var ruleB = await repo.CreateRuleAsync(
            projectId, "b", [AlertEventType.NewIssue, AlertEventType.Regression], [Level.Error, Level.Fatal]);
        await repo.AddChannelAsync(ruleB.Id, NotificationChannel.Webhook, "https://webhook.test/same");

        var webhook = new CapturingNotifier(NotificationChannel.Webhook);
        var dispatcher = new AlertDispatcher(
            repo, new ProjectRepository(pg.ConnectionString), [webhook], NullLogger<AlertDispatcher>.Instance);

        await dispatcher.DispatchAsync(projectId, AlertEventType.NewIssue, NewIssue(projectId, Level.Error));

        // Deduped per (transport, target): the destination gets the alert once, not once per matching rule.
        var sent = Assert.Single(webhook.Sent);
        Assert.Equal("https://webhook.test/same", sent.Target.Target);
    }
}
