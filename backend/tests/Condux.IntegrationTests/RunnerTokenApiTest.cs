using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Condux.IntegrationTests.Fixtures;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// Runner tokens over HTTP: an admin mints one, a cookie-less runner leases with only
/// <c>Authorization: Bearer</c>, the token shows as used, and revoking it cuts the runner off.
///
/// Slice 4a shipped without this: the lease tests drive the store directly and nothing ever minted
/// through the API, so runner_tokens carrying different column names than the shared repository speaks
/// went unnoticed until the first click in the dashboard answered 42703. Every mint/resolve/revoke path
/// of the shared repository gets a per-kind test for exactly that reason — a regression in one binding
/// stays green on the others.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RunnerTokenApiTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private HttpClient CreateClient() => ControlPlaneApp.Create(pg.ConnectionString).CreateClient();

    private async Task<(HttpClient Client, long OrgId)> ProvisionAsync()
    {
        var client = CreateClient();
        await ApiAuth.SignUpAsync(client);
        var orgResp = await client.PostAsJsonAsync("/api/orgs",
            new { slug = "acme-" + Guid.NewGuid().ToString("N"), name = "Acme" });
        var orgId = (await orgResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        await OrgSeed.SetTierAsync(pg.ConnectionString, orgId, 2);
        return (client, orgId);
    }

    private HttpClient BearerClient(string token)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task Mint_LeaseOverBearer_List_Revoke_RoundTrips()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var (client, orgId) = await ProvisionAsync();

        // Minted once, and the raw value is returned exactly once.
        var mintResp = await client.PostAsJsonAsync($"/api/orgs/{orgId}/runner-tokens",
            new { label = "build-server-1" });
        Assert.Equal(HttpStatusCode.OK, mintResp.StatusCode);
        var minted = await mintResp.Content.ReadFromJsonAsync<JsonElement>();
        var rawToken = minted.GetProperty("token").GetString()!;
        Assert.StartsWith("condux_run_", rawToken);

        // A runner with no cookie leases on the token alone. 204 is the healthy answer here: the org has
        // no queued work, and the point is that the credential resolved rather than 401ing.
        var runner = BearerClient(rawToken);
        var leaseResp = await runner.PostAsync("/api/runner/lease", content: null);
        Assert.Equal(HttpStatusCode.NoContent, leaseResp.StatusCode);

        // Listing never returns the secret, and records that the token has now been used.
        var listResp = await client.GetAsync($"/api/orgs/{orgId}/runner-tokens");
        var listed = (await listResp.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
        var row = Assert.Single(listed);
        Assert.False(row.TryGetProperty("token", out _));
        Assert.Equal("build-server-1", row.GetProperty("label").GetString());
        Assert.Equal(JsonValueKind.String, row.GetProperty("lastUsedAt").ValueKind);

        // Revoked, and the same token stops leasing immediately.
        var tokenId = row.GetProperty("id").GetString()!;
        var revokeResp = await client.DeleteAsync($"/api/orgs/{orgId}/runner-tokens/{tokenId}");
        Assert.True(revokeResp.IsSuccessStatusCode);

        var afterRevoke = await BearerClient(rawToken).PostAsync("/api/runner/lease", content: null);
        Assert.Equal(HttpStatusCode.Unauthorized, afterRevoke.StatusCode);
    }

    [Fact]
    public async Task A_token_of_another_kind_does_not_lease()
    {
        // All three token kinds ride one repository, so the boundary worth proving is that a kind is
        // scoped to its own table: an MCP token must not lease fix work however similar the shapes are.
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var (client, orgId) = await ProvisionAsync();
        var projResp = await client.PostAsJsonAsync($"/api/orgs/{orgId}/projects",
            new { slug = "backend", name = "Backend", platform = "python" });
        var projectId = (await projResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("project").GetProperty("id").GetInt64();
        var mintResp = await client.PostAsJsonAsync($"/api/projects/{projectId}/mcp-tokens",
            new { name = "Agent" });
        var mcpToken = (await mintResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("token").GetString()!;

        var resp = await BearerClient(mcpToken).PostAsync("/api/runner/lease", content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }
}
