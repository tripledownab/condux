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
/// The MCP triage tools (ADR-0046): an agent holding a <c>triage</c> token sets an issue's status and
/// leaves a note, and a <c>read</c> token can do neither.
///
/// The refusal is asserted twice on purpose, once against tools/list and once against tools/call. A server
/// that only filtered the published list would pass the first and fail the second, and a client that
/// hardcodes a tool name never reads the list, so the list is not access control.
/// </summary>
[Trait("Category", "Integration")]
public sealed class McpWriteToolsTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private HttpClient CreateClient() => ControlPlaneApp.Create(pg.ConnectionString).CreateClient();

    private async Task<(HttpClient Client, long OrgId, long ProjectId)> ProvisionAsync()
    {
        var client = CreateClient();
        await ApiAuth.SignUpAsync(client);
        var orgResp = await client.PostAsJsonAsync("/api/orgs",
            new { slug = "acme-" + Guid.NewGuid().ToString("N"), name = "Acme" });
        var orgId = (await orgResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        await OrgSeed.SetTierAsync(pg.ConnectionString, orgId, 2);
        return (client, orgId, await CreateProjectAsync(client, orgId, "Backend"));
    }

    private static async Task<long> CreateProjectAsync(HttpClient client, long orgId, string name)
    {
        var resp = await client.PostAsJsonAsync(
            $"/api/orgs/{orgId}/projects", new { name, platform = "python" });
        return (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("project").GetProperty("id").GetInt64();
    }

    private async Task<HttpClient> MintAgentAsync(HttpClient client, long projectId, string? capability)
    {
        var mint = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/mcp-tokens", new { name = "Claude", capability });
        Assert.Equal(HttpStatusCode.OK, mint.StatusCode);
        var minted = await mint.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(capability ?? "read", minted.GetProperty("capability").GetString());
        return McpRpc.BearerClient(CreateClient(), minted.GetProperty("token").GetString()!);
    }

    private static Task<JsonElement> RpcAsync(HttpClient client, string method, object? prms = null) =>
        McpRpc.CallMethodAsync(client, method, prms);

    private static Task<JsonElement> CallAsync(HttpClient agent, string name, object arguments) =>
        McpRpc.CallToolAsync(agent, name, arguments);

    private static string[] ToolNamesOf(JsonElement rpc) => McpRpc.ToolNames(rpc);

    private static bool IsError(JsonElement rpc) => McpRpc.IsError(rpc);

    private static string ErrorText(JsonElement rpc) => McpRpc.TextOf(rpc);

    private static JsonElement ToolJson(JsonElement rpc) => McpRpc.ToolJson(rpc);

    private async Task<Guid> SeedIssueAsync(long projectId, string fingerprint, string title)
    {
        var upsert = await new IssueRepository(pg.ConnectionString).UpsertAsync(
            projectId, new Grouping(fingerprint, title, "app.py:1"), Level.Error, DateTimeOffset.UtcNow);
        return upsert.PublicId;
    }

    private async Task<int> StatusOfAsync(long projectId, Guid issueId)
    {
        var found = await new IssueRepository(pg.ConnectionString).GetByPublicIdAsync(projectId, issueId);
        Assert.NotNull(found);
        return found.Value.Summary.Status;
    }

    [Fact]
    public async Task ReadToken_NeitherSeesNorReachesTheWriteTools()
    {
        var (client, _, projectId) = await ProvisionAsync();
        var issueId = await SeedIssueAsync(projectId, "fp-read", "TypeError: boom");
        var agent = await MintAgentAsync(client, projectId, capability: null);

        // Not published.
        var names = ToolNamesOf(await RpcAsync(agent, "tools/list"));
        Assert.Contains("list_issues", names);
        Assert.DoesNotContain("set_issue_status", names);
        Assert.DoesNotContain("add_issue_note", names);

        // And not reachable by naming them anyway.
        var status = await CallAsync(
            agent, "set_issue_status", new { issueId = issueId.ToString(), status = "resolved" });
        Assert.True(IsError(status));
        Assert.Contains("triage", ErrorText(status));

        var note = await CallAsync(
            agent, "add_issue_note", new { issueId = issueId.ToString(), body = "fixed it" });
        Assert.True(IsError(note));

        // The refusal is a refusal, not a silent no-op that reports failure after writing.
        Assert.Equal(1, await StatusOfAsync(projectId, issueId));
        var notes = await client.GetFromJsonAsync<JsonElement>(
            $"/api/projects/{projectId}/issues/{issueId}/notes");
        Assert.Equal(0, notes.GetArrayLength());
    }

    [Fact]
    public async Task TriageToken_SetsStatus_AndCanReopen()
    {
        var (client, _, projectId) = await ProvisionAsync();
        var issueId = await SeedIssueAsync(projectId, "fp-triage", "TypeError: boom");
        var agent = await MintAgentAsync(client, projectId, "triage");

        Assert.Contains("set_issue_status", ToolNamesOf(await RpcAsync(agent, "tools/list")));

        var resolved = await CallAsync(
            agent, "set_issue_status", new { issueId = issueId.ToString(), status = "resolved" });
        Assert.False(IsError(resolved));
        Assert.Equal(2, await StatusOfAsync(projectId, issueId));
        // The reply echoes the NAME. The tool takes named states because the stored 1/2/3 are a poor
        // contract for a model, and its description never explains them, so returning a number would
        // hand back a value the caller has no key for.
        Assert.Equal("resolved", ToolJson(resolved).GetProperty("status").GetString());

        var ignored = await CallAsync(
            agent, "set_issue_status", new { issueId = issueId.ToString(), status = "ignored" });
        Assert.False(IsError(ignored));
        Assert.Equal(3, await StatusOfAsync(projectId, issueId));

        // Reopening is offered so an agent can undo its own wrong call.
        var reopened = await CallAsync(
            agent, "set_issue_status", new { issueId = issueId.ToString(), status = "unresolved" });
        Assert.False(IsError(reopened));
        Assert.Equal(1, await StatusOfAsync(projectId, issueId));

        // An unknown state changes nothing.
        var bogus = await CallAsync(
            agent, "set_issue_status", new { issueId = issueId.ToString(), status = "wontfix" });
        Assert.True(IsError(bogus));
        Assert.Equal(1, await StatusOfAsync(projectId, issueId));
    }

    [Fact]
    public async Task TriageToken_AddsANote_AttributedToTheTokenRatherThanAUser()
    {
        var (client, _, projectId) = await ProvisionAsync();
        var issueId = await SeedIssueAsync(projectId, "fp-note", "TypeError: boom");
        var agent = await MintAgentAsync(client, projectId, "triage");

        var added = await CallAsync(agent, "add_issue_note",
            new { issueId = issueId.ToString(), body = "Root cause: unchecked None. Fixed in #42." });
        Assert.False(IsError(added));

        var notes = await client.GetFromJsonAsync<JsonElement>(
            $"/api/projects/{projectId}/issues/{issueId}/notes");
        Assert.Equal(1, notes.GetArrayLength());
        var note = notes[0];
        Assert.Equal("Root cause: unchecked None. Fixed in #42.", note.GetProperty("body").GetString());
        // The dashboard has a name to show, and it is the token's, not a blank that would read as a
        // note by someone who left.
        Assert.Equal("Claude", note.GetProperty("authorTokenName").GetString());
        Assert.Equal(JsonValueKind.Null, note.GetProperty("authorUserId").ValueKind);
        Assert.Equal(JsonValueKind.Null, note.GetProperty("authorEmail").ValueKind);

        // The same body limit the REST endpoint enforces, since both write the same column.
        var tooLong = await CallAsync(agent, "add_issue_note",
            new { issueId = issueId.ToString(), body = new string('x', 5_001) });
        Assert.True(IsError(tooLong));

        var empty = await CallAsync(agent, "add_issue_note",
            new { issueId = issueId.ToString(), body = "   " });
        Assert.True(IsError(empty));
    }

    [Fact]
    public async Task TriageToken_CannotWriteToAnotherProjectsIssue()
    {
        var (client, orgId, projectA) = await ProvisionAsync();
        var projectB = await CreateProjectAsync(client, orgId, "Other");

        var issueInB = await SeedIssueAsync(projectB, "fp-other", "Other boom");
        var agent = await MintAgentAsync(client, projectA, "triage");

        // The token is scoped to A, so B's issue does not exist as far as it is concerned.
        var attempt = await CallAsync(
            agent, "set_issue_status", new { issueId = issueInB.ToString(), status = "resolved" });
        Assert.True(IsError(attempt));
        Assert.Contains("not found", ErrorText(attempt));
        Assert.Equal(1, await StatusOfAsync(projectB, issueInB));
    }

    [Fact]
    public async Task EveryPublishedTool_HasAHandler()
    {
        var (client, _, projectId) = await ProvisionAsync();
        var agent = await MintAgentAsync(client, projectId, "triage");

        // The catalog and the dispatch switch are separate lists, so this walks the published catalog and
        // proves each name reaches a handler. Calling with no arguments is enough: a tool with a handler
        // fails on its arguments, one without fails on its name.
        foreach (var name in ToolNamesOf(await RpcAsync(agent, "tools/list")))
        {
            var result = await CallAsync(agent, name, new { });
            if (IsError(result))
            {
                Assert.DoesNotContain("unknown tool", ErrorText(result));
            }
        }
    }

    [Fact]
    public async Task AnOmittedCapabilityField_MintsARead_Token()
    {
        var (client, _, projectId) = await ProvisionAsync();

        // Not the same request as sending an explicit null: a client written before ADR-0046 sends no
        // such property at all, and it must keep getting exactly the token it used to get.
        var mint = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/mcp-tokens", new { name = "Legacy client" });
        Assert.Equal(HttpStatusCode.OK, mint.StatusCode);
        var minted = await mint.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("read", minted.GetProperty("capability").GetString());

        var agent = CreateClient();
        agent.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", minted.GetProperty("token").GetString()!);
        Assert.DoesNotContain("set_issue_status", ToolNamesOf(await RpcAsync(agent, "tools/list")));
    }

    [Fact]
    public async Task MintingAnUnrecognisedCapability_IsRefused()
    {
        var (client, _, projectId) = await ProvisionAsync();

        // Not silently downgraded to read: a client asking for authority we do not know about must not
        // walk away believing it got something.
        var resp = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/mcp-tokens", new { name = "x", capability = "admin" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("invalid_capability",
            (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task TokensMintedBeforeCapabilitiesExisted_StayReadOnly()
    {
        var (client, _, projectId) = await ProvisionAsync();
        var agent = await MintAgentAsync(client, projectId, capability: null);

        // The column defaults to read, which is what makes the migration unable to widen an existing
        // token. Minting without naming a capability takes that same default path.
        var listed = await client.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}/mcp-tokens");
        Assert.Equal("read", listed[0].GetProperty("capability").GetString());
        Assert.DoesNotContain("set_issue_status", ToolNamesOf(await RpcAsync(agent, "tools/list")));
    }
}
