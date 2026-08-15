using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Condux.Core.Events;
using Condux.Core.Grouping;
using Condux.Core.Plans;
using Condux.IntegrationTests.Fixtures;
using Condux.Storage.ClickHouse;
using Condux.Storage.Postgres;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// The lazy-loaded events endpoint: an issue's sampled events paginate newest-first with a hasMore flag,
/// the detail payload itself is now slim (just the latest event), and a bad page is rejected.
/// </summary>
[Trait("Category", "Integration")]
public sealed class IssueEventsApiTest(PostgresFixture pg, ClickHouseFixture ch)
    : IClassFixture<PostgresFixture>, IClassFixture<ClickHouseFixture>
{
    private async Task<(HttpClient Client, long ProjectId, string PublicId)> SeedAsync(int eventCount)
    {
        var client = ControlPlaneApp.Create(pg.ConnectionString, ch).CreateClient();
        await ApiAuth.SignUpAsync(client);
        var orgResp = await client.PostAsJsonAsync("/api/orgs",
            new { slug = "acme-" + Guid.NewGuid().ToString("N"), name = "Acme" });
        var orgId = (await orgResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        await OrgSeed.SetTierAsync(pg.ConnectionString, orgId, 2);
        var projResp = await client.PostAsJsonAsync($"/api/orgs/{orgId}/projects",
            new { name = "Backend", platform = "python" });
        var projectId = (await projResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("project").GetProperty("id").GetInt64();

        var grouping = new Grouping("fp-events", "TypeError: boom", "app.py:1");
        var upsert = await new IssueRepository(pg.ConnectionString)
            .UpsertAsync(projectId, grouping, Level.Error, DateTimeOffset.UtcNow);

        using var httpClient = new HttpClient();
        ClickHouseRegistration.Configure(httpClient, ch.BaseUrl, ch.ChUser, ch.ChPassword);
        var writer = new ClickHouseEventWriter(httpClient);
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < eventCount; i++)
        {
            var e = new Event
            {
                EventId = Guid.NewGuid().ToString("N"),
                Level = Level.Error,
                Message = $"event-{i}",
                TimestampUnixMs = now.AddSeconds(-i).ToUnixTimeMilliseconds(),
            };
            await writer.InsertAsync([ClickHouseEventWriter.ToRow(
                projectId.ToString(), (ulong)upsert.Id, e, grouping.Fingerprint,
                PlanCatalog.For(Tier.Free).RetentionDays)]);
        }

        var list = (await client.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}/issues"))
            .GetProperty("issues");
        return (client, projectId, list[0].GetProperty("id").GetString()!);
    }

    [Fact]
    public async Task Events_paginate_newest_first_with_hasMore_and_the_detail_stays_slim()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var (client, projectId, publicId) = await SeedAsync(5);
        var basePath = $"/api/projects/{projectId}/issues/{publicId}/events";

        var page1 = await client.GetFromJsonAsync<JsonElement>($"{basePath}?limit=2&offset=0");
        Assert.Equal(2, page1.GetProperty("events").GetArrayLength());
        Assert.True(page1.GetProperty("hasMore").GetBoolean());
        Assert.Equal("event-0", page1.GetProperty("events")[0].GetProperty("message").GetString());

        var lastPage = await client.GetFromJsonAsync<JsonElement>($"{basePath}?limit=2&offset=4");
        Assert.Equal(1, lastPage.GetProperty("events").GetArrayLength());
        Assert.False(lastPage.GetProperty("hasMore").GetBoolean());

        // The detail payload no longer carries every event — just the latest, for the inline evidence.
        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}/issues/{publicId}");
        Assert.Equal(1, detail.GetProperty("events").GetArrayLength());
    }

    [Fact]
    public async Task Events_rejects_a_bad_page()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var (client, projectId, publicId) = await SeedAsync(1);
        var resp = await client.GetAsync($"/api/projects/{projectId}/issues/{publicId}/events?limit=0");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
