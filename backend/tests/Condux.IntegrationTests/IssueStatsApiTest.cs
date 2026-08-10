using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Condux.Core.Events;
using Condux.Core.Grouping;
using Condux.IntegrationTests.Fixtures;
using Condux.Storage.ClickHouse;
using Condux.Storage.Postgres;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// HTTP-level test of the exact issue histogram (#102): counting rows written per event (the path the
/// consumer takes for EVERY event, unsampled) come back through the stats endpoint as a zero-filled
/// hourly series, and an invalid window is rejected.
/// </summary>
[Trait("Category", "Integration")]
public sealed class IssueStatsApiTest(PostgresFixture pg, ClickHouseFixture ch)
    : IClassFixture<PostgresFixture>, IClassFixture<ClickHouseFixture>
{
    [Fact]
    public async Task Stats_ReturnsAZeroFilledHourlySeriesWithExactCounts()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var client = ControlPlaneApp.Create(pg.ConnectionString, ch).CreateClient();
        await ApiAuth.SignUpAsync(client);
        var orgResp = await client.PostAsJsonAsync("/api/orgs",
            new { slug = "stats-" + Guid.NewGuid().ToString("N"), name = "Stats", tier = 1 });
        var orgId = (await orgResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        var projResp = await client.PostAsJsonAsync($"/api/orgs/{orgId}/projects",
            new { slug = "svc", name = "Svc", platform = "python" });
        var projectId = (await projResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("project").GetProperty("id").GetInt64();

        var repo = new IssueRepository(pg.ConnectionString);
        var upsert = await repo.UpsertAsync(
            projectId, new Grouping("fp-stats", "Boom", "run"), Level.Error, DateTimeOffset.UtcNow);

        // Three events this hour, one the hour before — written the way the consumer counts every event.
        using var httpClient = new HttpClient();
        ClickHouseRegistration.Configure(httpClient, ch.BaseUrl, ch.ChUser, ch.ChPassword);
        var now = DateTimeOffset.UtcNow;
        var project = projectId.ToString();
        await new ClickHouseIssueStatsWriter(httpClient).InsertAsync(
        [
            ClickHouseIssueStatsWriter.ToRow(project, (ulong)upsert.Id, now),
            ClickHouseIssueStatsWriter.ToRow(project, (ulong)upsert.Id, now),
            ClickHouseIssueStatsWriter.ToRow(project, (ulong)upsert.Id, now),
            ClickHouseIssueStatsWriter.ToRow(project, (ulong)upsert.Id, now.AddHours(-1)),
        ]);

        var stats = await client.GetFromJsonAsync<JsonElement>(
            $"/api/projects/{project}/issues/{upsert.PublicId}/stats?hours=24");

        var buckets = stats.GetProperty("buckets");
        Assert.Equal(24, buckets.GetArrayLength()); // zero-filled, complete window
        Assert.Equal(3_600, stats.GetProperty("bucketSeconds").GetInt32());
        // Look buckets up by timestamp (not index) so an hour rollover mid-test cannot flake.
        var hourTs = now.ToUnixTimeSeconds() / 3_600 * 3_600;
        Assert.Equal(3L, CountAt(buckets, hourTs)); // exact, all events counted (no sampling)
        Assert.Equal(1L, CountAt(buckets, hourTs - 3_600));
        Assert.Equal(4L, Enumerable.Range(0, 24).Sum(i => buckets[i].GetProperty("count").GetInt64()));

        var bad = await client.GetAsync($"/api/projects/{project}/issues/{upsert.PublicId}/stats?hours=25");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        // The batch sparkline endpoint returns the same series keyed by the PUBLIC id, in one call.
        var sparklines = await client.GetFromJsonAsync<JsonElement>($"/api/projects/{project}/issues/stats");
        var entry = Assert.Single(sparklines.GetProperty("issues").EnumerateArray().ToList());
        Assert.Equal(upsert.PublicId.ToString(), entry.GetProperty("issueId").GetString());
        Assert.Equal(24, entry.GetProperty("buckets").GetArrayLength());
        Assert.Equal(3L, CountAt(entry.GetProperty("buckets"), hourTs));
    }

    private static long CountAt(JsonElement buckets, long ts)
    {
        for (var i = 0; i < buckets.GetArrayLength(); i++)
        {
            if (buckets[i].GetProperty("ts").GetInt64() == ts)
            {
                return buckets[i].GetProperty("count").GetInt64();
            }
        }
        return -1;
    }
}
