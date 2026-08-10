using Condux.Core.Alerting;
using Condux.Core.Events;
using Condux.IntegrationTests.Fixtures;
using Condux.Storage.Postgres;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// Alert rules + channels against a real Postgres (#54): create/list rules, attach channels, load the
/// engine's "enabled rules with channels" view, and confirm disable hides a rule and delete cascades.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AlertRuleRepositoryTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private async Task<long> SeedProjectAsync()
    {
        var org = await new OrgRepository(pg.ConnectionString).CreateAsync(
            "acme-" + Guid.NewGuid().ToString("N"), "Acme", 2);
        var project = await new ProjectRepository(pg.ConnectionString).CreateAsync(
            org.Id, "Backend", "python");
        return project.Id;
    }

    [Fact]
    public async Task Create_AddChannel_And_LoadEnabledWithChannels()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var repo = new AlertRuleRepository(pg.ConnectionString);
        var projectId = await SeedProjectAsync();

        var rule = await repo.CreateRuleAsync(
            projectId, "prod errors", [AlertEventType.NewIssue], [Level.Error, Level.Fatal]);
        await repo.AddChannelAsync(rule.Id, NotificationChannel.Slack, "https://hooks.slack.test/x");

        var rules = await repo.ListRulesByProjectAsync(projectId);
        Assert.Single(rules);
        Assert.Equal("prod errors", rules[0].Name);
        Assert.Equal(new[] { Level.Error, Level.Fatal }, rules[0].Levels);
        Assert.Equal(new[] { AlertEventType.NewIssue }, rules[0].Events);

        var enabled = await repo.ListEnabledWithChannelsByProjectAsync(projectId);
        var loaded = Assert.Single(enabled);
        Assert.Equal(rule.Id, loaded.Rule.Id);
        var channel = Assert.Single(loaded.Channels);
        Assert.Equal(NotificationChannel.Slack, channel.Channel);
        Assert.Equal("https://hooks.slack.test/x", channel.Target);
    }

    [Fact]
    public async Task DisabledRule_IsExcludedFromEnabledWithChannels()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var repo = new AlertRuleRepository(pg.ConnectionString);
        var projectId = await SeedProjectAsync();

        var rule = await repo.CreateRuleAsync(
            projectId, "muted", [AlertEventType.NewIssue, AlertEventType.Regression], [Level.Info]);
        await repo.AddChannelAsync(rule.Id, NotificationChannel.Email, "ops@acme.test");
        Assert.True(await repo.UpdateRuleAsync(
            projectId, rule.Id, rule.Name, rule.Events, rule.Levels, enabled: false));

        Assert.Empty(await repo.ListEnabledWithChannelsByProjectAsync(projectId));
        Assert.False((await repo.GetRuleAsync(projectId, rule.Id))!.Enabled);
    }

    [Fact]
    public async Task DeleteRule_CascadesChannels()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var repo = new AlertRuleRepository(pg.ConnectionString);
        var projectId = await SeedProjectAsync();

        var rule = await repo.CreateRuleAsync(
            projectId, "temp", [AlertEventType.NewIssue, AlertEventType.Regression], [Level.Error]);
        await repo.AddChannelAsync(rule.Id, NotificationChannel.Webhook, "https://webhook.test/x");

        Assert.True(await repo.DeleteRuleAsync(projectId, rule.Id));
        Assert.Empty(await repo.ListRulesByProjectAsync(projectId));
        Assert.Empty(await repo.ListChannelsByRuleAsync(rule.Id)); // channels cascaded with the rule
    }
}
