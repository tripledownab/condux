extern alias demoseed;
using Condux.Core.WeeklySummaries;
using Condux.IntegrationTests.Fixtures;
using Condux.Notifications;
using Condux.Storage.ClickHouse;
using Condux.Storage.Postgres;
using Npgsql;
using Xunit;
using DemoCatalog = demoseed::Condux.DemoSeed.DemoCatalog;
using DemoSeeder = demoseed::Condux.DemoSeed.DemoSeeder;
using SeedStatus = demoseed::Condux.DemoSeed.SeedStatus;

namespace Condux.IntegrationTests;

/// <summary>
/// Drives the demo-seed tool end-to-end against real Postgres + ClickHouse, then runs the weekly-summary
/// composer over the seeded org — proving the tool produces exactly the data the dashboard + digest read, and
/// that the category movement (new / regressed / resolved / Conductor activity) matches the catalog. Also
/// checks the fresh-DB idempotency guard.
/// </summary>
[Trait("Category", "Integration")]
public sealed class DemoSeedTest(PostgresFixture pg, ClickHouseFixture ch)
    : IClassFixture<PostgresFixture>, IClassFixture<ClickHouseFixture>
{
    [Fact]
    public async Task Seeds_the_demo_account_and_data_the_weekly_summary_reads()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        using var http = Http();
        await new DemoSeeder(pg.ConnectionString, http).RunAsync();

        var user = await new UserRepository(pg.ConnectionString).GetByEmailAsync("demo@condux.dev");
        Assert.NotNull(user);
        Assert.NotNull(user!.OnboardedAt); // onboarded, so no gate
        Assert.Equal(DemoCatalog.Issues.Count, await ScalarAsync("SELECT count(*) FROM issues"));
        Assert.Equal(DemoCatalog.Fixes.Count, await ScalarAsync("SELECT count(*) FROM fix_suggestions"));

        // The weekly digest reads exactly this data — the movement must match the catalog.
        var orgId = await ScalarAsync("SELECT id FROM orgs WHERE slug = 'demo'");
        var summary = await new WeeklySummaryComposer(
                new ProjectRepository(pg.ConnectionString), new IssueRepository(pg.ConnectionString),
                new ClickHouseIssueStatsReader(http), new ClickHouseEventReader(http),
                new PostgresFixStore(pg.ConnectionString))
            .ComposeAsync(orgId, "Demo", DateTimeOffset.UtcNow);

        Assert.True(summary.HadActivity);
        Assert.True(summary.Events > 0);
        Assert.NotEmpty(summary.TopIssues);
        Assert.Equal(DemoCatalog.Issues.Count(i => i.FirstSeenDaysAgo < 7), summary.NewIssues);
        Assert.Equal(DemoCatalog.Issues.Count(i => i.Status == SeedStatus.Regressed), summary.Regressions);
        Assert.Equal(DemoCatalog.Issues.Count(i => i.Status == SeedStatus.Resolved), summary.Resolved);
        Assert.Equal(DemoCatalog.Fixes.Count(f => f.CreatedDaysAgo < 7), summary.Fixes.Proposed);
        Assert.Equal(DemoCatalog.Fixes.Count(f => f.MergedDaysAgo is < 7), summary.Fixes.PrsMerged);
        Assert.Equal(DemoCatalog.Fixes.Count(f => f.VerifiedDaysAgo is < 7), summary.Fixes.AutoResolved);

        // Fresh-DB guard: re-running against a seeded DB adds nothing.
        await new DemoSeeder(pg.ConnectionString, http).RunAsync();
        Assert.Equal(DemoCatalog.Issues.Count, await ScalarAsync("SELECT count(*) FROM issues"));
    }

    private HttpClient Http()
    {
        var http = new HttpClient();
        ClickHouseRegistration.Configure(http, ch.BaseUrl, ch.ChUser, ch.ChPassword);
        return http;
    }

    private async Task<long> ScalarAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }
}
