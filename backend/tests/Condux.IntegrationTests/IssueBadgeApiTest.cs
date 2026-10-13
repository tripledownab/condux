using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Condux.Core.Events;
using Condux.Core.Grouping;
using Condux.IntegrationTests.Fixtures;
using Condux.Storage.Postgres;
using Npgsql;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// The per-user "new issues" nav badge (ADR-0030): new-count counts issues activated (new or regressed)
/// since the user's watermark; POST seen clears it. The watermark is stamped with the database's clock
/// (<c>now()</c>), so every activation time here is set relative to that clock rather than the host's: a
/// database clock behind the host's would leave issues activated "just now" newer than the watermark
/// written after them.
/// </summary>
[Trait("Category", "Integration")]
public sealed class IssueBadgeApiTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
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

    private async Task<DateTimeOffset> DatabaseNowAsync()
    {
        await using var conn = new NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT now()", conn);
        return new DateTimeOffset((DateTime)(await cmd.ExecuteScalarAsync())!, TimeSpan.Zero);
    }

    private static async Task<int> NewCountAsync(HttpClient client, long projectId) =>
        (await client.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}/issues/new-count"))
            .GetProperty("count").GetInt32();

    [Fact]
    public async Task Counts_new_and_regressed_since_seen_and_clears()
    {
        var (client, projectId) = await ProvisionAsync();
        var repo = new IssueRepository(pg.ConnectionString);
        var dbNow = await DatabaseNowAsync();
        var t0 = dbNow.AddMinutes(-1);

        // Two brand-new issues, activated before the watermark the next step writes; with no watermark yet,
        // both read as new.
        var a = await repo.UpsertAsync(projectId, new Grouping("fp-a", "A boom", "a"), Level.Error, t0);
        await repo.UpsertAsync(projectId, new Grouping("fp-b", "B boom", "b"), Level.Error, t0);
        Assert.Equal(2, await NewCountAsync(client, projectId));

        // Opening the list marks it seen (now), clearing the badge.
        var seen = await client.PostAsync($"/api/projects/{projectId}/issues/seen", null);
        Assert.Equal(HttpStatusCode.NoContent, seen.StatusCode);
        Assert.Equal(0, await NewCountAsync(client, projectId));

        // A new issue activated after the watermark counts again.
        await repo.UpsertAsync(projectId, new Grouping("fp-c", "C boom", "c"), Level.Error, dbNow.AddMinutes(5));
        Assert.Equal(1, await NewCountAsync(client, projectId));

        // Regressing an earlier issue (resolve, then a new event reopens it) re-activates it, so it counts
        // too — the badge surfaces regressions, not just brand-new issues.
        Assert.True(await repo.UpdateStatusAsync(projectId, a.PublicId, 2));
        var reopened = await repo.UpsertAsync(
            projectId, new Grouping("fp-a", "A boom", "a"), Level.Error, dbNow.AddMinutes(10));
        Assert.True(reopened.Reopened);
        Assert.Equal(2, await NewCountAsync(client, projectId));
    }
}
