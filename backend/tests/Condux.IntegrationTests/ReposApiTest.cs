using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Condux.Core.CveScanning;
using Condux.IntegrationTests.Fixtures;
using Condux.Storage.Postgres;
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
    // the server (a GitHub-less self-host still links freely). The shared settings are enough here, since
    // the guard reads installations from Postgres and never mints a JWT.
    private static void EnableGitHubApp(IWebHostBuilder b) => GithubAppSettings.Apply(b);

    private async Task<(HttpClient Client, long ProjectId)> ProvisionAsync(
        Action<IWebHostBuilder>? configure = null)
    {
        var client = ControlPlaneApp.Create(pg.ConnectionString, configure: configure).CreateClient();
        await ApiAuth.SignUpAsync(client);
        var orgResp = await client.PostAsJsonAsync("/api/orgs",
            new { slug = "acme-" + Guid.NewGuid().ToString("N"), name = "Acme" });
        var orgId = (await orgResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        await OrgSeed.SetTierAsync(pg.ConnectionString, orgId, 2);
        var projResp = await client.PostAsJsonAsync($"/api/orgs/{orgId}/projects",
            new { slug = "backend", name = "Backend", platform = "python" });
        var projectId = (await projResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("project").GetProperty("id").GetInt64();
        return (client, projectId);
    }

    [Fact]
    public async Task LinkRepo_AddMapping_RecordRelease_RoundTrips()
    {
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
            new { repoLinkId = repoId, version = "1.2.3", commitSha = "abc1234" });
        Assert.Equal(HttpStatusCode.Created, relResp.StatusCode);

        var repos = await client.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}/repos");
        Assert.Equal("acme/api", repos[0].GetProperty("repoFullName").GetString());

        var mappings = await client.GetFromJsonAsync<JsonElement>(
            $"/api/projects/{projectId}/repos/{repoId}/code-mappings");
        Assert.Equal("/app/dist/", mappings[0].GetProperty("stackRoot").GetString());

        var releases = await client.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}/releases");
        Assert.Equal("1.2.3", releases[0].GetProperty("version").GetString());
        Assert.Equal("abc1234", releases[0].GetProperty("commitSha").GetString());
    }

    // The token route always trimmed and refused a blank; this route stored whatever it was sent, so a
    // commit like "abc " formed a frame link that answered 404.
    [Theory]
    [InlineData("1.2.3", "abc", "invalid_commit")]
    [InlineData("1.2.3", "abc 1234", "invalid_commit")]
    [InlineData("1.2.3", "", "invalid_commit")]
    [InlineData("", "abc1234", "invalid_version")]
    public async Task RecordRelease_RefusesWhatIsNotARelease(string version, string commit, string error)
    {
        var (client, projectId) = await ProvisionAsync();
        var linkResp = await client.PostAsJsonAsync($"/api/projects/{projectId}/repos",
            new { repoFullName = "acme/api", defaultBranch = "main" });
        var repoId = (await linkResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        var resp = await client.PostAsJsonAsync($"/api/projects/{projectId}/releases",
            new { repoLinkId = repoId, version, commitSha = commit });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal(error, (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.Empty((await client.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}/releases")).EnumerateArray());
    }

    [Fact]
    public async Task RecordRelease_StoresTheVersionAndCommitTrimmed()
    {
        var (client, projectId) = await ProvisionAsync();
        var linkResp = await client.PostAsJsonAsync($"/api/projects/{projectId}/repos",
            new { repoFullName = "acme/api", defaultBranch = "main" });
        var repoId = (await linkResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync($"/api/projects/{projectId}/releases",
            new { repoLinkId = repoId, version = " 1.2.3 ", commitSha = " abc1234\n" })).StatusCode);

        var release = (await client.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}/releases"))[0];
        Assert.Equal("1.2.3", release.GetProperty("version").GetString());
        Assert.Equal("abc1234", release.GetProperty("commitSha").GetString());
    }

    // An empty branch names no branch: a URL that puts it in a path gets an empty segment, which
    // RepoPaths.EscapeSegments refuses, and one that puts it in a query asks for nothing.
    [Fact]
    public async Task LinkRepo_StoresABlankBranchAsMain()
    {
        var (client, projectId) = await ProvisionAsync();

        var linkResp = await client.PostAsJsonAsync($"/api/projects/{projectId}/repos",
            new { repoFullName = "acme/api", defaultBranch = " " });

        Assert.Equal(HttpStatusCode.Created, linkResp.StatusCode);
        Assert.Equal("main", (await linkResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("defaultBranch").GetString());
    }

    // A padded branch names a branch that is not there, so it is trimmed as the update route trims it.
    [Fact]
    public async Task LinkRepo_TrimsAPaddedBranch()
    {
        var (client, projectId) = await ProvisionAsync();

        var linkResp = await client.PostAsJsonAsync($"/api/projects/{projectId}/repos",
            new { repoFullName = "acme/api", defaultBranch = " release/2 " });

        Assert.Equal("release/2", (await linkResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("defaultBranch").GetString());
    }

    [Fact]
    public async Task LinkRepo_WithGitHubAppConfiguredButNotConnected_Returns409()
    {
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
        var (client, projectId) = await ProvisionAsync();

        var resp = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/repos/{Guid.NewGuid()}/code-mappings",
            new { stackRoot = "/x/", sourceRoot = "y/" });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task DeleteCodeMapping_RemovesOne_LeavingTheRest()
    {
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

    // A deployment with no GitHub App has no scan to offer and nothing an org could do about it, so it
    // says nothing rather than warning under every repository.
    [Fact]
    public async Task CveFindings_WithoutAGitHubApp_ReturnsEmptyWithNoReason()
    {
        var (client, projectId) = await ProvisionAsync();
        var repoId = (await (await client.PostAsJsonAsync($"/api/projects/{projectId}/repos",
            new { repoFullName = "acme/api" })).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString();

        var resp = await client.GetAsync($"/api/projects/{projectId}/repos/{repoId}/cve-findings");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(body.GetProperty("findings").EnumerateArray());
        Assert.False(body.GetProperty("truncated").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("unavailable").ValueKind);
    }

    // With the App configured, an org that has not connected it is told so, since the empty list would
    // otherwise read as a repository with no alerts. The link is written directly because linking through
    // the API needs the connection this test leaves out.
    [Fact]
    public async Task CveFindings_WithoutGitHubConnected_ReturnsEmptyAndSaysWhy()
    {
        var (client, projectId) = await ProvisionAsync(EnableGitHubApp);
        var repo = await new RepoLinkRepository(pg.ConnectionString).LinkAsync(projectId, "acme/api", "main");

        var resp = await client.GetAsync($"/api/projects/{projectId}/repos/{repo.Id}/cve-findings");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(body.GetProperty("findings").EnumerateArray());
        Assert.False(body.GetProperty("truncated").GetBoolean());
        Assert.Equal((int)CveScanUnavailable.NotConnected, body.GetProperty("unavailable").GetInt32());
    }

    [Fact]
    public async Task SuggestedMappings_WithoutGitHubConnected_ReturnsEmpty()
    {
        var (client, projectId) = await ProvisionAsync();
        var repoId = (await (await client.PostAsJsonAsync($"/api/projects/{projectId}/repos",
            new { repoFullName = "acme/api" })).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString();

        // No GitHub installation for the org → nothing to derive, but a clean 200 (not a 500).
        var resp = await client.GetAsync($"/api/projects/{projectId}/repos/{repoId}/suggested-mappings");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(body.GetProperty("suggestions").EnumerateArray());
        Assert.Equal("not_connected", body.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task UnlinkRepo_RemovesTheRepoAndCascadesItsMappings()
    {
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
