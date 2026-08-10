using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Condux.IntegrationTests.Fixtures;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// Scoped release tokens over HTTP: an admin mints a token, then a fresh cookie-less client (like CI)
/// records a release with only <c>Authorization: Bearer</c>, the token shows as used, and revoking it
/// makes further use 401. Also covers a bad token and a project with no linked repo.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ReleaseTokenApiTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private HttpClient CreateClient() => ControlPlaneApp.Create(pg.ConnectionString).CreateClient();

    private async Task<(HttpClient Client, long ProjectId)> ProvisionAsync()
    {
        var client = CreateClient();
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

    private HttpClient BearerClient(string token)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task MintToken_RecordReleaseViaBearer_List_Revoke_RoundTrips()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var (client, projectId) = await ProvisionAsync();
        await client.PostAsJsonAsync($"/api/projects/{projectId}/repos",
            new { repoFullName = "acme/api", defaultBranch = "main" });

        // Admin mints a release token; the raw value is returned once.
        var mintResp = await client.PostAsJsonAsync($"/api/projects/{projectId}/release-tokens",
            new { name = "GitHub Actions" });
        Assert.Equal(HttpStatusCode.OK, mintResp.StatusCode);
        var rawToken = (await mintResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        Assert.StartsWith("condux_rel_", rawToken);

        // A cookie-less client (like CI) records a release with only the Bearer token — no login, no ids.
        var ci = BearerClient(rawToken);
        var relResp = await ci.PostAsJsonAsync("/api/releases", new { version = "1.4.2", commitSha = "deadbeef" });
        Assert.Equal(HttpStatusCode.Created, relResp.StatusCode);
        Assert.Equal("1.4.2",
            (await relResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetString());

        // The release is visible on the project.
        var releases = await client.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}/releases");
        Assert.Equal("1.4.2", releases[0].GetProperty("version").GetString());

        // The token lists as used, not revoked, and never exposes the raw value.
        var list = await client.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}/release-tokens");
        Assert.Equal("GitHub Actions", list[0].GetProperty("name").GetString());
        Assert.False(list[0].GetProperty("revoked").GetBoolean());
        Assert.NotNull(list[0].GetProperty("lastUsedAt").GetString());
        Assert.False(list[0].TryGetProperty("token", out _));
        var tokenId = list[0].GetProperty("id").GetString();

        // Revoking it makes further use 401.
        var del = await client.DeleteAsync($"/api/projects/{projectId}/release-tokens/{tokenId}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);
        var afterRevoke = await ci.PostAsJsonAsync("/api/releases", new { version = "1.4.3", commitSha = "cafe" });
        Assert.Equal(HttpStatusCode.Unauthorized, afterRevoke.StatusCode);
    }

    [Fact]
    public async Task RecordRelease_WithAnUnknownToken_Returns401()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var ci = BearerClient("condux_rel_not-a-real-token");
        var resp = await ci.PostAsJsonAsync("/api/releases", new { version = "1.0.0", commitSha = "abc" });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task RecordRelease_WhenNoRepoLinked_Returns400()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var (client, projectId) = await ProvisionAsync();
        var mintResp = await client.PostAsJsonAsync($"/api/projects/{projectId}/release-tokens", new { name = "CI" });
        var rawToken = (await mintResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;

        var ci = BearerClient(rawToken);
        var resp = await ci.PostAsJsonAsync("/api/releases", new { version = "1.0.0", commitSha = "abc" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
