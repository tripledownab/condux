using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Condux.IntegrationTests.Fixtures;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// Scoped MCP tokens over HTTP: an admin mints one, a cookie-less client (an AI agent) reaches
/// <c>/api/mcp</c> with only <c>Authorization: Bearer</c>, the token shows as used, and revoking it makes
/// further use unauthorized.
///
/// This existed for release tokens and not for these, which meant token resolution for MCP had no test
/// over it at all. That gap is the reason it is here: minting, resolving and revoking now run through one
/// shared repository, so a regression in it would otherwise reach production silently on this path while
/// the release-token test stayed green.
/// </summary>
[Trait("Category", "Integration")]
public sealed class McpTokenApiTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
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

    private static HttpContent ToolsList() => JsonContent.Create(
        new { jsonrpc = "2.0", id = 1, method = "tools/list" });

    [Fact]
    public async Task Mint_UseOverBearer_List_Revoke_RoundTrips()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var (client, projectId) = await ProvisionAsync();

        // Minted once, and the raw value is returned exactly once.
        var mintResp = await client.PostAsJsonAsync($"/api/projects/{projectId}/mcp-tokens",
            new { name = "Claude Desktop" });
        Assert.Equal(HttpStatusCode.OK, mintResp.StatusCode);
        var minted = await mintResp.Content.ReadFromJsonAsync<JsonElement>();
        var rawToken = minted.GetProperty("token").GetString()!;
        Assert.StartsWith("condux_mcp_", rawToken);

        // An agent with no cookie reaches the MCP endpoint on the token alone. This is the path the
        // shared repository resolves, so it is the one worth exercising end to end.
        var agent = BearerClient(rawToken);
        var callResp = await agent.PostAsync("/api/mcp", ToolsList());
        Assert.Equal(HttpStatusCode.OK, callResp.StatusCode);

        // Listing never returns the secret, and records that the token has now been used.
        var listResp = await client.GetAsync($"/api/projects/{projectId}/mcp-tokens");
        var listed = (await listResp.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
        var row = Assert.Single(listed);
        Assert.False(row.TryGetProperty("token", out _));
        Assert.Equal(JsonValueKind.String, row.GetProperty("lastUsedAt").ValueKind);

        // Revoked, and the same token stops working immediately.
        var tokenId = row.GetProperty("id").GetString()!;
        var revokeResp = await client.DeleteAsync($"/api/projects/{projectId}/mcp-tokens/{tokenId}");
        Assert.True(revokeResp.IsSuccessStatusCode);

        var afterRevoke = await BearerClient(rawToken).PostAsync("/api/mcp", ToolsList());
        Assert.Equal(HttpStatusCode.Unauthorized, afterRevoke.StatusCode);
    }

    [Fact]
    public async Task An_unknown_token_is_unauthorized()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);

        var resp = await BearerClient("condux_mcp_notarealtokenatall").PostAsync("/api/mcp", ToolsList());

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task A_token_of_another_kind_does_not_authenticate_here()
    {
        // Release and MCP tokens now share one repository. Resolving is scoped by table, so a release
        // token must not open an MCP session however similar the two look.
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var (client, projectId) = await ProvisionAsync();
        var mintResp = await client.PostAsJsonAsync($"/api/projects/{projectId}/release-tokens",
            new { name = "CI" });
        var releaseToken = (await mintResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("token").GetString()!;

        var resp = await BearerClient(releaseToken).PostAsync("/api/mcp", ToolsList());

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }
}
