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
            new { slug = "acme-" + Guid.NewGuid().ToString("N"), name = "Acme" });
        var orgId = (await orgResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        await OrgSeed.SetTierAsync(pg.ConnectionString, orgId, 2);
        return (client, orgId);
    }

    private static async Task<long> CreateProjectAsync(HttpClient client, long orgId, string name)
    {
        var resp = await client.PostAsJsonAsync($"/api/orgs/{orgId}/projects", new { name, platform = "python" });
        return (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("project").GetProperty("id").GetInt64();
    }

    private HttpClient BearerClient(string token) => McpRpc.BearerClient(CreateClient(), token);

    private static Task<JsonElement> RpcAsync(HttpClient client, string method, object? prms = null) =>
        McpRpc.CallMethodAsync(client, method, prms);

    private static JsonElement ToolJson(JsonElement rpc) => McpRpc.ToolJson(rpc);

    [Fact]
    public async Task Initialize_ListTools_And_CallListIssues_ScopedToTheTokensProject()
    {
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
        var names = McpRpc.ToolNames(await RpcAsync(agent, "tools/list"));
        Assert.Contains("list_issues", names);
        Assert.Contains("get_issue", names);
        Assert.Contains("list_issue_events", names);

        // tools/call list_issues returns only project A's issue.
        var call = await RpcAsync(agent, "tools/call",
            new { name = "list_issues", arguments = new { limit = 50 } });
        Assert.False(McpRpc.IsError(call));
        var listed = ToolJson(call).GetProperty("issues");
        Assert.Equal(1, listed.GetArrayLength());
        Assert.Equal("A boom", listed[0].GetProperty("title").GetString());
    }

    [Fact]
    public async Task ToolErrors_SurfaceAsIsError_NotProtocolErrors()
    {
        var (client, orgId) = await ProvisionAsync();
        var projectId = await CreateProjectAsync(client, orgId, "A");
        var raw = (await (await client.PostAsJsonAsync($"/api/projects/{projectId}/mcp-tokens", new { name = "x" }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        var agent = BearerClient(raw);

        // An unknown tool and a missing issue are tool errors (isError), not JSON-RPC protocol errors.
        var unknown = await RpcAsync(agent, "tools/call", new { name = "delete_everything", arguments = new { } });
        Assert.True(McpRpc.IsError(unknown));

        var missing = await RpcAsync(agent, "tools/call",
            new { name = "get_issue", arguments = new { issueId = Guid.NewGuid().ToString() } });
        Assert.True(McpRpc.IsError(missing));

        // An out-of-range page is a tool error (validated before the lookup, so no issue is required).
        var badPage = await RpcAsync(agent, "tools/call", new
        {
            name = "list_issue_events",
            arguments = new { issueId = Guid.NewGuid().ToString(), limit = 9999 },
        });
        Assert.True(McpRpc.IsError(badPage));
    }

    /// <summary>
    /// A request whose string is half a surrogate pair parses as JSON and threw when the endpoint or a tool
    /// read it, answering 500. Wherever the string sits it is now a JSON-RPC parse error, like any other
    /// body that is not JSON.
    /// </summary>
    [Theory]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"\uD800"}""")]
    [InlineData("""{"jsonrpc":"2.0","id":"\uD800","method":"ping"}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"\uD800"}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"get_issue","arguments":{"issueId":"\uDC00"}}}""")]
    public async Task ARequestWhoseStringIsNotText_IsAParseError(string body)
    {
        var resp = await PostRawAsync(await MintTokenAsync(), System.Text.Encoding.UTF8.GetBytes(body));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var error = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error");
        Assert.Equal(-32700, error.GetProperty("code").GetInt32());
    }

    /// <summary>
    /// Valid JSON of the wrong kind where the endpoint expects an object or a string threw on the read, a 500.
    /// A root that is not an object is an invalid request, and a tool name that is not a string invalid params.
    /// </summary>
    [Theory]
    [InlineData("""[{"jsonrpc":"2.0","id":1,"method":"ping"}]""", -32600)]
    [InlineData("1", -32600)]
    [InlineData("null", -32600)]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":5}}""", -32602)]
    public async Task ARequestOfTheWrongKind_IsAJsonRpcError(string body, int code)
    {
        var resp = await PostRawAsync(await MintTokenAsync(), System.Text.Encoding.UTF8.GetBytes(body));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var error = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error");
        Assert.Equal(code, error.GetProperty("code").GetInt32());
    }

    /// <summary>
    /// U+0000 is valid text, so the endpoint reads it, and Postgres refuses it in the query that follows.
    /// That threw out of the tool as a 500; it is the caller's text, so it is answered 400 like the REST
    /// routes, by the same one place.
    /// </summary>
    [Fact]
    public async Task AToolArgumentPostgresCannotStore_IsTheCallersMistake()
    {
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"list_issues","arguments":{"query":"a\u0000b"}}}""";

        var resp = await PostRawAsync(await MintTokenAsync(), System.Text.Encoding.UTF8.GetBytes(body));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("text_not_storable",
            (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    /// <summary>A body that opens with a UTF-8 byte order mark is still read, as it was before.</summary>
    [Fact]
    public async Task ARequestOpeningWithAByteOrderMark_IsServed()
    {
        byte[] body = [0xEF, 0xBB, 0xBF, .. System.Text.Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":1,"method":"ping"}""")];

        var resp = await PostRawAsync(await MintTokenAsync(), body);

        var rpc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(rpc.TryGetProperty("result", out _), rpc.ToString());
    }

    private async Task<string> MintTokenAsync()
    {
        var (client, orgId) = await ProvisionAsync();
        var projectId = await CreateProjectAsync(client, orgId, "A");
        return (await (await client.PostAsJsonAsync($"/api/projects/{projectId}/mcp-tokens", new { name = "x" }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
    }

    private Task<HttpResponseMessage> PostRawAsync(string token, byte[] body)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return BearerClient(token).PostAsync("/api/mcp", content);
    }

    [Fact]
    public async Task NoOrRevokedToken_Is401()
    {
        var (client, orgId) = await ProvisionAsync();
        var projectId = await CreateProjectAsync(client, orgId, "A");

        // No bearer at all.
        var anon = await CreateClient().PostAsJsonAsync("/api/mcp",
            new { jsonrpc = "2.0", id = 1, method = "tools/list" });
        Assert.Equal(HttpStatusCode.Unauthorized, anon.StatusCode);
        // RFC 9110 15.5.2: a 401 must carry a challenge, and a client reads the scheme from it.
        Assert.Equal("Bearer", anon.Headers.WwwAuthenticate.Single().Scheme);

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
        Assert.Equal("Bearer", revoked.Headers.WwwAuthenticate.Single().Scheme);
        // RFC 6750 3.1: a token that was sent and refused is named; the missing one above is not.
        Assert.Contains("error=\"invalid_token\"", revoked.Headers.WwwAuthenticate.Single().Parameter);
        Assert.DoesNotContain("error", anon.Headers.WwwAuthenticate.Single().Parameter);
    }

    [Fact]
    public async Task ServerCard_NeedsNoToken_AndPublishesExactlyWhatATriageTokenIsServed()
    {
        var (client, orgId) = await ProvisionAsync();
        var projectId = await CreateProjectAsync(client, orgId, "A");
        var raw = (await (await client.PostAsJsonAsync($"/api/projects/{projectId}/mcp-tokens",
                new { name = "x", capability = "triage" }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        var agent = BearerClient(raw);

        var resp = await CreateClient().GetAsync("/.well-known/mcp/server-card.json");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var card = await resp.Content.ReadFromJsonAsync<JsonElement>();

        // Compared as JSON values, definition for definition (the two responses escape characters
        // differently, which is not a difference), so a tool on one side only fails here rather than in a
        // directory's listing.
        var listed = await RpcAsync(agent, "tools/list");
        var served = listed.GetProperty("result").GetProperty("tools");
        Assert.True(JsonElement.DeepEquals(served, card.GetProperty("tools")));
        Assert.Contains("set_issue_status", McpRpc.ToolNames(listed));

        var init = (await RpcAsync(agent, "initialize")).GetProperty("result").GetProperty("serverInfo");
        Assert.True(JsonElement.DeepEquals(init, card.GetProperty("serverInfo")));
        Assert.True(card.GetProperty("authentication").GetProperty("required").GetBoolean());
        Assert.Equal("bearer", card.GetProperty("authentication").GetProperty("schemes")[0].GetString());
    }

    [Fact]
    public async Task AForeignBrowserOrigin_Is403_WhileTheDeploymentsOwnOriginAndNoOriginAreServed()
    {
        var (client, orgId) = await ProvisionAsync();
        var projectId = await CreateProjectAsync(client, orgId, "A");
        var raw = (await (await client.PostAsJsonAsync($"/api/projects/{projectId}/mcp-tokens", new { name = "x" }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        // The base URL is an internationalised domain, which a browser sends in punycode. The CORS origin
        // carries a port, which is the split-origin dev shape and must survive the comparison.
        var app = ControlPlaneApp.Create(pg.ConnectionString, appBaseUrl: "https://bücher.example.test/",
            configure: b => b.UseSetting("CONDUX_CORS_ORIGINS", "http://localhost:3000"));

        async Task<HttpStatusCode> ListToolsFrom(string? origin)
        {
            var agent = McpRpc.BearerClient(app.CreateClient(), raw);
            if (origin is not null)
                agent.DefaultRequestHeaders.Add("Origin", origin);
            var resp = await agent.PostAsJsonAsync("/api/mcp", new { jsonrpc = "2.0", id = 1, method = "tools/list" });
            return resp.StatusCode;
        }

        // MCP 2025-06-18 basic/transports: the server must validate Origin, against DNS rebinding.
        Assert.Equal(HttpStatusCode.Forbidden, await ListToolsFrom("https://evil.example"));
        Assert.Equal(HttpStatusCode.Forbidden, await ListToolsFrom("https://xn--bcher-kva.example.test:8443"));
        Assert.Equal(HttpStatusCode.OK, await ListToolsFrom("https://xn--bcher-kva.example.test"));
        Assert.Equal(HttpStatusCode.OK, await ListToolsFrom("http://localhost:3000"));
        Assert.Equal(HttpStatusCode.Forbidden, await ListToolsFrom("http://localhost"));
        Assert.Equal(HttpStatusCode.OK, await ListToolsFrom(null));
    }
}
