using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Condux.Core.Events;
using Condux.Core.Grouping;
using Condux.IntegrationTests.Fixtures;
using Condux.Storage.Postgres;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// The MCP server (ADR-0029) over HTTP: an admin mints a per-project MCP token, then a cookie-less agent
/// speaks JSON-RPC to <c>/api/mcp</c> with only <c>Authorization: Bearer</c> — initialize, tools/list, and
/// tools/call. Asserts the tools are read-only + scoped to the token's project (a second project's issues
/// never leak), tool errors surface as isError results, and no/revoked token is 401.
/// </summary>
[Trait("Category", "Integration")]
public sealed class McpApiTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private HttpClient CreateClient() => ControlPlaneApp.Create(pg.ConnectionString).CreateClient();

    private async Task<(HttpClient Client, long OrgId)> ProvisionAsync()
    {
        var client = CreateClient();
        await ApiAuth.SignUpAsync(client);
        var orgResp = await client.PostAsJsonAsync("/api/orgs",
            new { slug = "acme-" + Guid.NewGuid().ToString("N"), name = "Acme", tier = 2 });
        var orgId = (await orgResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        return (client, orgId);
    }

    private static async Task<long> CreateProjectAsync(HttpClient client, long orgId, string name)
    {
        var resp = await client.PostAsJsonAsync($"/api/orgs/{orgId}/projects", new { name, platform = "python" });
        return (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("project").GetProperty("id").GetInt64();
    }

    private HttpClient BearerClient(string token)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<JsonElement> RpcAsync(HttpClient client, string method, object? prms = null)
    {
        var resp = await client.PostAsJsonAsync("/api/mcp",
            new { jsonrpc = "2.0", id = 1, method, @params = prms });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    // The text payload a tools/call result carries (our tools serialize their data as JSON text).
    private static JsonElement ToolJson(JsonElement rpc) =>
        JsonDocument.Parse(rpc.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!)
            .RootElement;

    [Fact]
    public async Task Initialize_ListTools_And_CallListIssues_ScopedToTheTokensProject()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var (client, orgId) = await ProvisionAsync();
        var projectA = await CreateProjectAsync(client, orgId, "A");
        var projectB = await CreateProjectAsync(client, orgId, "B");

        // Seed one issue in each project; the token is for A, so B's issue must never surface.
        var repo = new IssueRepository(pg.ConnectionString);
        await repo.UpsertAsync(projectA, new Grouping("fp-a", "A boom", "a"), Level.Error, DateTimeOffset.UtcNow);
        await repo.UpsertAsync(projectB, new Grouping("fp-b", "B boom", "b"), Level.Error, DateTimeOffset.UtcNow);

        var mint = await client.PostAsJsonAsync($"/api/projects/{projectA}/mcp-tokens", new { name = "Claude" });
        Assert.Equal(HttpStatusCode.OK, mint.StatusCode);
        var raw = (await mint.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        Assert.StartsWith("condux_mcp_", raw);
        var agent = BearerClient(raw);

        // initialize advertises the protocol + server.
        var init = await RpcAsync(agent, "initialize");
        Assert.False(string.IsNullOrEmpty(init.GetProperty("result").GetProperty("protocolVersion").GetString()));
        Assert.Equal("condux",
            init.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());

        // tools/list names the read-only tools.
        var tools = (await RpcAsync(agent, "tools/list")).GetProperty("result").GetProperty("tools");
        var names = tools.EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToArray();
        Assert.Contains("list_issues", names);
        Assert.Contains("get_issue", names);
        Assert.Contains("list_issue_events", names);

        // tools/call list_issues returns only project A's issue.
        var call = await RpcAsync(agent, "tools/call",
            new { name = "list_issues", arguments = new { limit = 50 } });
        Assert.False(call.GetProperty("result").GetProperty("isError").GetBoolean());
        var listed = ToolJson(call).GetProperty("issues");
        Assert.Equal(1, listed.GetArrayLength());
        Assert.Equal("A boom", listed[0].GetProperty("title").GetString());
    }

    [Fact]
    public async Task ToolErrors_SurfaceAsIsError_NotProtocolErrors()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var (client, orgId) = await ProvisionAsync();
        var projectId = await CreateProjectAsync(client, orgId, "A");
        var raw = (await (await client.PostAsJsonAsync($"/api/projects/{projectId}/mcp-tokens", new { name = "x" }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        var agent = BearerClient(raw);

        // An unknown tool and a missing issue are tool errors (isError), not JSON-RPC protocol errors.
        var unknown = await RpcAsync(agent, "tools/call", new { name = "delete_everything", arguments = new { } });
        Assert.True(unknown.GetProperty("result").GetProperty("isError").GetBoolean());

        var missing = await RpcAsync(agent, "tools/call",
            new { name = "get_issue", arguments = new { issueId = Guid.NewGuid().ToString() } });
        Assert.True(missing.GetProperty("result").GetProperty("isError").GetBoolean());

        // An out-of-range page is a tool error (validated before the lookup, so no issue is required).
        var badPage = await RpcAsync(agent, "tools/call", new
        {
            name = "list_issue_events",
            arguments = new { issueId = Guid.NewGuid().ToString(), limit = 9999 },
        });
        Assert.True(badPage.GetProperty("result").GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task NoOrRevokedToken_Is401()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var (client, orgId) = await ProvisionAsync();
        var projectId = await CreateProjectAsync(client, orgId, "A");

        // No bearer at all.
        var anon = await CreateClient().PostAsJsonAsync("/api/mcp",
            new { jsonrpc = "2.0", id = 1, method = "tools/list" });
        Assert.Equal(HttpStatusCode.Unauthorized, anon.StatusCode);

        // A minted-then-revoked token no longer authenticates.
        var mint = await client.PostAsJsonAsync($"/api/projects/{projectId}/mcp-tokens", new { name = "x" });
        var minted = await mint.Content.ReadFromJsonAsync<JsonElement>();
        var raw = minted.GetProperty("token").GetString()!;
        var tokenId = minted.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.DeleteAsync($"/api/projects/{projectId}/mcp-tokens/{tokenId}")).StatusCode);

        var revoked = await BearerClient(raw).PostAsJsonAsync("/api/mcp",
            new { jsonrpc = "2.0", id = 1, method = "tools/list" });
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
    }
}
