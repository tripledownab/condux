using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Condux.IntegrationTests.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// Connect-repo + error→code linking API (#89) over HTTP, tenancy enforced: provision an org+project,
/// link a repo, add a code mapping, record a release, and read them all back. Also checks that a
/// mapping for an unknown repo 404s.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ReposApiTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    // A GitHub-App-enabled host: the linkRepo connection guard only engages when the App is configured on
    // the server (a GitHub-less self-host still links freely). A generated key is enough — the guard reads
    // installations from Postgres and never mints a JWT.
    private static readonly string GithubKeyPem = RSA.Create(2048).ExportRSAPrivateKeyPem();

    private static void EnableGitHubApp(IWebHostBuilder b)
    {
        b.UseSetting("CONDUX_GITHUB_CLIENT_ID", "Iv1.test");
        b.UseSetting("CONDUX_GITHUB_WEBHOOK_SECRET", "test-webhook-secret");
        b.UseSetting("CONDUX_GITHUB_APP_SLUG", "condux-test");
        b.UseSetting("CONDUX_GITHUB_PRIVATE_KEY", GithubKeyPem);
    }

    private async Task<(HttpClient Client, long ProjectId)> ProvisionAsync(
        Action<IWebHostBuilder>? configure = null)
    {
        var client = ControlPlaneApp.Create(pg.ConnectionString, configure: configure).CreateClient();
        await ApiAuth.SignUpAsync(client);
        var orgResp = await client.PostAsJsonAsync("/api/orgs",
            new { slug = "acme-" + Guid.NewGuid().ToString("N"), name = "Acme", tier = 2 });
        var orgId = (await orgResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        var projResp = await client.PostAsJsonAsync($"/api/orgs/{orgId}/projects",
            new { slug = "backend", name = "Backend", platform = "python" });
        var projectId = (await projResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("project").GetProperty("id").GetInt64();
        return (client, projectId);
    }

    [Fact]
    public async Task LinkRepo_AddMapping_RecordRelease_RoundTrips()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var (client, projectId) = await ProvisionAsync();

        var linkResp = await client.PostAsJsonAsync($"/api/projects/{projectId}/repos",
            new { repoFullName = "acme/api", defaultBranch = "main" });
        Assert.Equal(HttpStatusCode.Created, linkResp.StatusCode);
        var repoId = (await linkResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        var mapResp = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/repos/{repoId}/code-mappings",
            new { stackRoot = "/app/dist/", sourceRoot = "src/" });
        Assert.Equal(HttpStatusCode.Created, mapResp.StatusCode);

        var relResp = await client.PostAsJsonAsync($"/api/projects/{projectId}/releases",
            new { repoLinkId = repoId, version = "1.2.3", commitSha = "abc123" });
        Assert.Equal(HttpStatusCode.Created, relResp.StatusCode);

        var repos = await client.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}/repos");
        Assert.Equal("acme/api", repos[0].GetProperty("repoFullName").GetString());

        var mappings = await client.GetFromJsonAsync<JsonElement>(
            $"/api/projects/{projectId}/repos/{repoId}/code-mappings");
        Assert.Equal("/app/dist/", mappings[0].GetProperty("stackRoot").GetString());

        var releases = await client.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}/releases");
        Assert.Equal("1.2.3", releases[0].GetProperty("version").GetString());
        Assert.Equal("abc123", releases[0].GetProperty("commitSha").GetString());
    }

    [Fact]
    public async Task LinkRepo_WithGitHubAppConfiguredButNotConnected_Returns409()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var (client, projectId) = await ProvisionAsync(EnableGitHubApp);

        // The App is configured on this server but the org has installed no installation, so the Conductor
        // has no token to reach any repo. Linking is refused rather than storing a dead link.
        var resp = await client.PostAsJsonAsync($"/api/projects/{projectId}/repos",
            new { repoFullName = "acme/api", defaultBranch = "main" });
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Equal("github_not_connected",
            (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task CodeMapping_ForUnknownRepo_Returns404()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var (client, projectId) = await ProvisionAsync();

        var resp = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/repos/{Guid.NewGuid()}/code-mappings",
            new { stackRoot = "/x/", sourceRoot = "y/" });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task DeleteCodeMapping_RemovesOne_LeavingTheRest()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var (client, projectId) = await ProvisionAsync();
        var repoId = (await (await client.PostAsJsonAsync($"/api/projects/{projectId}/repos",
            new { repoFullName = "acme/api" })).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString();

        async Task<string> AddMappingAsync(string stackRoot) =>
            (await (await client.PostAsJsonAsync($"/api/projects/{projectId}/repos/{repoId}/code-mappings",
                new { stackRoot, sourceRoot = "src/" })).Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("id").GetString()!;
        var keep = await AddMappingAsync("/app/keep/");
        var drop = await AddMappingAsync("/app/drop/");

        var del = await client.DeleteAsync(
            $"/api/projects/{projectId}/repos/{repoId}/code-mappings/{drop}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        var mappings = await client.GetFromJsonAsync<JsonElement>(
            $"/api/projects/{projectId}/repos/{repoId}/code-mappings");
        var remaining = mappings.EnumerateArray().Select(m => m.GetProperty("id").GetString()).ToList();
        Assert.Equal([keep], remaining);

        // Deleting an already-gone mapping 404s.
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.DeleteAsync($"/api/projects/{projectId}/repos/{repoId}/code-mappings/{drop}")).StatusCode);
    }

    [Fact]
    public async Task CveFindings_WithoutGitHubConnected_ReturnsEmpty()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var (client, projectId) = await ProvisionAsync();
        var repoId = (await (await client.PostAsJsonAsync($"/api/projects/{projectId}/repos",
            new { repoFullName = "acme/api" })).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString();

        // No GitHub installation → nothing to scan, but a clean 200 (not a 500).
        var resp = await client.GetAsync($"/api/projects/{projectId}/repos/{repoId}/cve-findings");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Empty((await resp.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray());
    }

    [Fact]
    public async Task SuggestedMappings_WithoutGitHubConnected_ReturnsEmpty()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var (client, projectId) = await ProvisionAsync();
        var repoId = (await (await client.PostAsJsonAsync($"/api/projects/{projectId}/repos",
            new { repoFullName = "acme/api" })).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString();

        // No GitHub installation for the org → nothing to derive, but a clean 200 (not a 500).
        var resp = await client.GetAsync($"/api/projects/{projectId}/repos/{repoId}/suggested-mappings");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Empty((await resp.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray());
    }

    [Fact]
    public async Task UnlinkRepo_RemovesTheRepoAndCascadesItsMappings()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var (client, projectId) = await ProvisionAsync();
        var repoId = (await (await client.PostAsJsonAsync($"/api/projects/{projectId}/repos",
            new { repoFullName = "acme/api" })).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString();
        await client.PostAsJsonAsync($"/api/projects/{projectId}/repos/{repoId}/code-mappings",
            new { stackRoot = "/app/", sourceRoot = "src/" });

        var del = await client.DeleteAsync($"/api/projects/{projectId}/repos/{repoId}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        // The repo is gone from the project, and its mappings cascaded (the repo now 404s).
        Assert.Empty(
            (await client.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}/repos")).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"/api/projects/{projectId}/repos/{repoId}/code-mappings")).StatusCode);

        // Unlinking a repo that is not there 404s.
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.DeleteAsync($"/api/projects/{projectId}/repos/{repoId}")).StatusCode);
    }
}
