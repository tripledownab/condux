using System.Globalization;
using Condux.Core.Events;
using Condux.Core.Plans;
using Condux.IntegrationTests.Fixtures;
using Condux.Storage.ClickHouse;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// Validates per-tier retention (#73) in ClickHouse: an event's retention_days column is stored from the
/// plan tier, and the table TTL is column-driven (references retention_days), so ClickHouse expires each
/// row on its own tier's schedule during background merges — no cron. Opt-in via Category=Integration.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ClickHouseRetentionTest(ClickHouseFixture ch) : IClassFixture<ClickHouseFixture>
{
    [Fact]
    public async Task Event_stores_its_tier_retention_and_the_table_ttl_is_column_driven()
    {
        using var http = new HttpClient();
        ClickHouseRegistration.Configure(http, ch.BaseUrl, ch.ChUser, ch.ChPassword);
        var writer = new ClickHouseEventWriter(http);

        var freeRetention = PlanCatalog.For(Tier.Free).RetentionDays;
        var e = new Event
        {
            EventId = "ret1",
            Level = Level.Error,
            Message = "boom",
            TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        await writer.InsertAsync([ClickHouseEventWriter.ToRow("42", 7UL, e, "fp-ret", freeRetention)]);

        // The row carries the tier's retention, stamped at ingest.
        var stored = await QueryAsync("SELECT retention_days FROM condux.events WHERE project_id = '42'");
        Assert.Equal(freeRetention.ToString(CultureInfo.InvariantCulture), stored.Trim());

        // The table TTL references the column (per-row), not a flat interval — this is what makes ClickHouse
        // expire each event on its tier's schedule with no scheduler.
        var ddl = await QueryAsync(
            "SELECT create_table_query FROM system.tables WHERE database = 'condux' AND name = 'events'");
        Assert.Contains("TTL", ddl);
        Assert.Contains("retention_days", ddl);
    }

    private async Task<string> QueryAsync(string sql)
    {
        using var http = new HttpClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{ch.BaseUrl}/")
        {
            Content = new StringContent(sql),
        };
        req.Headers.Add("X-ClickHouse-User", ch.ChUser);
        req.Headers.Add("X-ClickHouse-Key", ch.ChPassword);
        using var resp = await http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync();
    }
}
