using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Condux.Core.Events;
using Condux.Core.Grouping;
using Condux.IntegrationTests.Fixtures;
using Condux.Storage.Postgres;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// The per-user "new issues" nav badge (ADR-0030): new-count counts issues activated (new or regressed)
/// since the user's watermark; POST seen clears it. Timestamps are explicit so the assertions are
/// deterministic (no reliance on wall-clock ordering for the post-seen issues).
/// </summary>
[Trait("Category", "Integration")]
public sealed class IssueBadgeApiTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private async Task<(HttpClient Client, long ProjectId)> ProvisionAsync()
    {
        var client = ControlPlaneApp.Create(pg.ConnectionString).CreateClient();
        await ApiAuth.SignUpAsync(client);
        var orgResp = await client.PostAsJsonAsync("/api/orgs",
            new { slug = "acme-" + Guid.NewGuid().ToString("N"), name = "Acme", tier = 2 });
        var orgId = (await orgResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        var projResp = await client.PostAsJsonAsync($"/api/orgs/{orgId}/projects",
            new { name = "Backend", platform = "python" });
        var projectId = (await projResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("project").GetProperty("id").GetInt64();
        return (client, projectId);
    }

    private static async Task<int> NewCountAsync(HttpClient client, long projectId) =>
        (await client.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}/issues/new-count"))
            .GetProperty("count").GetInt32();

    [Fact]
    public async Task Counts_new_and_regressed_since_seen_and_clears()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var (client, projectId) = await ProvisionAsync();
        var repo = new IssueRepository(pg.ConnectionString);
        var t0 = DateTimeOffset.UtcNow;

        // Two brand-new issues; with no watermark yet, both read as new.
        var a = await repo.UpsertAsync(projectId, new Grouping("fp-a", "A boom", "a"), Level.Error, t0);
        await repo.UpsertAsync(projectId, new Grouping("fp-b", "B boom", "b"), Level.Error, t0);
        Assert.Equal(2, await NewCountAsync(client, projectId));

        // Opening the list marks it seen (now), clearing the badge.
        var seen = await client.PostAsync($"/api/projects/{projectId}/issues/seen", null);
        Assert.Equal(HttpStatusCode.NoContent, seen.StatusCode);
        Assert.Equal(0, await NewCountAsync(client, projectId));

        // A new issue activated after the watermark counts again (explicit future timestamp so the
        // ordering is deterministic regardless of the seen call's server clock).
        await repo.UpsertAsync(projectId, new Grouping("fp-c", "C boom", "c"), Level.Error, t0.AddMinutes(5));
        Assert.Equal(1, await NewCountAsync(client, projectId));

        // Regressing an earlier issue (resolve, then a new event reopens it) re-activates it, so it counts
        // too — the badge surfaces regressions, not just brand-new issues.
        Assert.True(await repo.UpdateStatusAsync(projectId, a.PublicId, 2));
        var reopened = await repo.UpsertAsync(
            projectId, new Grouping("fp-a", "A boom", "a"), Level.Error, t0.AddMinutes(10));
        Assert.True(reopened.Reopened);
        Assert.Equal(2, await NewCountAsync(client, projectId));
    }
}
