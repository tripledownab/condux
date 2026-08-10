using Condux.Core.Alerting;
using Condux.Core.OrgNotifications;
using Condux.IntegrationTests.Fixtures;
using Condux.Notifications;
using Condux.Storage.Postgres;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// The Conductor-pause notify path (#130) against real Postgres: a pause notice is delivered to the org's
/// configured channels once, then throttled for the same reason within the window (a different reason
/// still goes out). The throttle's window boundary is checked directly.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ConductorPauseNotifierTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private sealed class CapturingNotifier : INotifier
    {
        public List<(string Target, string Subject, string Body)> Sent { get; } = [];
        public NotificationChannel Channel => NotificationChannel.Webhook;

        public Task SendAsync(
            AlertChannel target, AlertNotification notification, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SendMessageAsync(
            string target, string subject, string body, CancellationToken cancellationToken = default)
        {
            Sent.Add((target, subject, body));
            return Task.CompletedTask;
        }
    }

    private async Task<long> SeedOrgWithWebhookAsync(string url)
    {
        var org = await new OrgRepository(pg.ConnectionString)
            .CreateAsync("org-" + Guid.NewGuid().ToString("N"), "Acme", tier: 1);
        await new OrgNotificationChannelRepository(pg.ConnectionString)
            .AddAsync(org.Id, NotificationChannel.Webhook, url);
        return org.Id;
    }

    [Fact]
    public async Task Notifies_TheOrgsChannels_OncePerReasonPerWindow()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var orgId = await SeedOrgWithWebhookAsync("https://hooks.test/w");
        var capturing = new CapturingNotifier();
        var notifier = new ConductorPauseNotifier(
            new PostgresPauseNotifyThrottle(pg.ConnectionString),
            new OrgNotificationDispatcher(
                new OrgNotificationChannelRepository(pg.ConnectionString), [capturing],
                NullLogger<OrgNotificationDispatcher>.Instance),
            NullLogger<ConductorPauseNotifier>.Instance);

        await notifier.NotifyAsync(orgId, "Acme", ConductorPauseReason.CostCapReached);
        var sent = Assert.Single(capturing.Sent);
        Assert.Equal("https://hooks.test/w", sent.Target);
        // Assert against the copy function, not a hardcoded substring, so a wording change can't silently rot
        // this test (it did: the subject was renamed in #172 and this ran nowhere, so it went unnoticed).
        Assert.Equal(ConductorPauseText.Subject(ConductorPauseReason.CostCapReached), sent.Subject);

        // Same reason again, inside the window → throttled, nothing new delivered.
        await notifier.NotifyAsync(orgId, "Acme", ConductorPauseReason.CostCapReached);
        Assert.Single(capturing.Sent);

        // A different reason is tracked independently → it does go out, with its own copy.
        await notifier.NotifyAsync(orgId, "Acme", ConductorPauseReason.AllowanceExhausted);
        Assert.Equal(2, capturing.Sent.Count);
        Assert.Equal(
            ConductorPauseText.Subject(ConductorPauseReason.AllowanceExhausted), capturing.Sent[1].Subject);
    }

    [Fact]
    public async Task Throttle_AllowsAgainOnlyAfterTheWindow()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var orgId = await SeedOrgWithWebhookAsync("https://hooks.test/w");
        var throttle = new PostgresPauseNotifyThrottle(pg.ConnectionString);
        var window = TimeSpan.FromHours(24);
        var now = DateTimeOffset.UtcNow;

        Assert.True(await throttle.TryAcquireAsync(orgId, ConductorPauseReason.CostCapReached, now, window));
        Assert.False(await throttle.TryAcquireAsync(orgId, ConductorPauseReason.CostCapReached, now, window));
        Assert.True(await throttle.TryAcquireAsync(
            orgId, ConductorPauseReason.CostCapReached, now.AddHours(25), window));
    }
}
