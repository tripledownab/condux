using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Condux.IntegrationTests.Fixtures;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// The supply-chain CVE-fix API (#117 slice 2) over HTTP, tenancy enforced. CI never reaches real GitHub,
/// so these cover the reachable-without-GitHub paths: a repo with no installation cannot start a bump
/// (a clean 409, not a 500 or a burnt allowance), the runs list is empty and 404s for an unknown repo.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CveFixApiTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private HttpClient CreateClient() => ControlPlaneApp.Create(pg.ConnectionString).CreateClient();

    private async Task<(HttpClient Client, long ProjectId, string RepoId)> ProvisionRepoAsync()
    {
        var client = CreateClient();
        await ApiAuth.SignUpAsync(client);
        var orgResp = await client.PostAsJsonAsync("/api/orgs",
            new { slug = "acme-" + Guid.NewGuid().ToString("N"), name = "Acme", tier = 2 }); // Business: AiFixes on
        var orgId = (await orgResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        var projResp = await client.PostAsJsonAsync($"/api/orgs/{orgId}/projects",
            new { slug = "backend", name = "Backend", platform = "node" });
        var projectId = (await projResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("project").GetProperty("id").GetInt64();
        var repoId = (await (await client.PostAsJsonAsync($"/api/projects/{projectId}/repos",
            new { repoFullName = "acme/api" })).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString()!;
        return (client, projectId, repoId);
    }

    [Fact]
    public async Task StartCveFix_WithoutGitHubConnected_Returns409()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var (client, projectId, repoId) = await ProvisionRepoAsync();

        var resp = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/repos/{repoId}/cve-fixes", new { ghsaId = "GHSA-jf85-cpcp-j695" });

        // The org tier allows AI fixes, but there is no GitHub installation to read the advisory or open a
        // PR — a clean 409, and no run is recorded.
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Equal("github_not_connected",
            (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());

        var runs = await client.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}/repos/{repoId}/cve-fixes");
        Assert.Empty(runs.EnumerateArray());
    }

    [Fact]
    public async Task ListCveFixes_ForUnknownRepo_Returns404()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var (client, projectId, _) = await ProvisionRepoAsync();

        var resp = await client.GetAsync($"/api/projects/{projectId}/repos/{Guid.NewGuid()}/cve-fixes");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task StartCveFix_ForUnknownRepo_Returns404()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var (client, projectId, _) = await ProvisionRepoAsync();

        var resp = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/repos/{Guid.NewGuid()}/cve-fixes", new { ghsaId = "GHSA-x" });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
