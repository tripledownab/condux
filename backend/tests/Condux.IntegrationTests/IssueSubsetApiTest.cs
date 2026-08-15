using System.Net.Http.Json;
using System.Text.Json;
using Condux.Core.Events;
using Condux.Core.Grouping;
using Condux.IntegrationTests.Fixtures;
using Condux.Storage.Postgres;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// The server-side issue subset (Sentry-style): the list filters (is:unresolved, level:), sorts and
/// paginates in SQL, and a counts endpoint returns facet totals — so the dashboard never loads every issue.
/// </summary>
[Trait("Category", "Integration")]
public sealed class IssueSubsetApiTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private async Task<(HttpClient Client, long ProjectId)> ProvisionAsync()
    {
        var client = ControlPlaneApp.Create(pg.ConnectionString).CreateClient();
        await ApiAuth.SignUpAsync(client);
        var orgResp = await client.PostAsJsonAsync("/api/orgs",
            new { slug = "acme-" + Guid.NewGuid().ToString("N"), name = "Acme" });
        var orgId = (await orgResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        await OrgSeed.SetTierAsync(pg.ConnectionString, orgId, 2);
        var projResp = await client.PostAsJsonAsync($"/api/orgs/{orgId}/projects",
            new { name = "Backend", platform = "python" });
        var projectId = (await projResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("project").GetProperty("id").GetInt64();
        return (client, projectId);
    }

    [Fact]
    public async Task Filters_paginates_and_counts_server_side()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var (client, projectId) = await ProvisionAsync();
        var repo = new IssueRepository(pg.ConnectionString);
        var now = DateTimeOffset.UtcNow;

        // Three unresolved errors + one resolved warning.
        await repo.UpsertAsync(projectId, new Grouping("fp-a", "A boom", "a"), Level.Error, now);
        await repo.UpsertAsync(projectId, new Grouping("fp-b", "B boom", "b"), Level.Error, now.AddSeconds(-1));
        await repo.UpsertAsync(projectId, new Grouping("fp-c", "C boom", "c"), Level.Error, now.AddSeconds(-2));
        var d = await repo.UpsertAsync(projectId, new Grouping("fp-d", "D warn", "d"), Level.Warning, now.AddSeconds(-3));
        Assert.True(await repo.UpdateStatusAsync(projectId, d.PublicId, 2)); // resolve the warning

        // Unfiltered page of 2 (newest first) with more remaining.
        var page1 = await client.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}/issues?limit=2");
        Assert.Equal(2, page1.GetProperty("issues").GetArrayLength());
        Assert.True(page1.GetProperty("hasMore").GetBoolean());
        Assert.Equal("A boom", page1.GetProperty("issues")[0].GetProperty("title").GetString());

        // is:unresolved → only the three unresolved, server-side.
        var unresolved = await client.GetFromJsonAsync<JsonElement>(
            $"/api/projects/{projectId}/issues?q=is:unresolved");
        Assert.Equal(3, unresolved.GetProperty("issues").GetArrayLength());
        Assert.False(unresolved.GetProperty("hasMore").GetBoolean());

        // level:warning → just the one warning.
        var warnings = await client.GetFromJsonAsync<JsonElement>(
            $"/api/projects/{projectId}/issues?q=level:warning");
        Assert.Equal(1, warnings.GetProperty("issues").GetArrayLength());
        Assert.Equal("D warn", warnings.GetProperty("issues")[0].GetProperty("title").GetString());

        // Facet counts: 3 unresolved + 1 resolved by status; 3 error + 1 warning by level.
        var counts = await client.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}/issues/counts");
        Assert.Equal(3, counts.GetProperty("byStatus").GetProperty("1").GetInt64());
        Assert.Equal(1, counts.GetProperty("byStatus").GetProperty("2").GetInt64());
        Assert.Equal(3, counts.GetProperty("byLevel").GetProperty("4").GetInt64());
        Assert.Equal(1, counts.GetProperty("byLevel").GetProperty("3").GetInt64());
    }
}
