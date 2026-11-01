using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Condux.Core.Events;
using Condux.Core.Grouping;
using Condux.IntegrationTests.Fixtures;
using Condux.Storage.Postgres;
using Condux.Tests.Shared;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// The triage-event producers (alert events): resolving an issue fires a rule that subscribes to the
/// Resolved event, delivered over a real socket to a webhook. Proves the control-plane (not just the
/// ingest consumer) dispatches alerts. Assigning is the sibling path, sharing the same helper.
///
/// Both producers are here because there are now two ways to resolve an issue, the dashboard PATCH and the
/// MCP triage tool (ADR-0046), and they must be indistinguishable to a subscriber. A tool that skipped the
/// alert rules would look like it worked.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AlertTriageProducerTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
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
            new { slug = "backend", name = "Backend", platform = "python" });
        var projectId = (await projResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("project").GetProperty("id").GetInt64();
        return (client, projectId);
    }

    // A rule firing on the Resolved event (3) at Error (4), delivering to a webhook channel (3).
    private static async Task AddResolvedRuleAsync(HttpClient client, long projectId, string url)
    {
        var createResp = await client.PostAsJsonAsync($"/api/projects/{projectId}/alert-rules",
            new { name = "resolutions", events = new[] { 3 }, levels = new[] { 4 } });
        var ruleId = (await createResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        await client.PostAsJsonAsync($"/api/projects/{projectId}/alert-rules/{ruleId}/channels",
            new { channel = 3, target = url });
    }

    // Run the resolve and return the one webhook body it produced. Fails the test if none arrives.
    private static async Task<string> CaptureAsync(LoopbackHttpReceiver receiver, Func<Task> resolve)
    {
        var next = receiver.NextRequestAsync();
        await resolve();
        return (await next.WaitAsync(TimeSpan.FromSeconds(5))).Body;
    }

    private async Task<Guid> SeedErrorIssueAsync(long projectId, string fingerprint, string title)
    {
        var upsert = await new IssueRepository(pg.ConnectionString).UpsertAsync(
            projectId, new Grouping(fingerprint, title, "app.py:1"), Level.Error, DateTimeOffset.UtcNow);
        return upsert.PublicId;
    }

    [Fact]
    public async Task Resolving_an_issue_fires_a_Resolved_rule_to_a_real_webhook()
    {
        var (client, projectId) = await ProvisionAsync();
        using var receiver = LoopbackHttpReceiver.Start();
        await AddResolvedRuleAsync(client, projectId, receiver.Url);

        // Issues come from ingest, not an API, so seed one directly and grab its public id.
        var issueId = await SeedErrorIssueAsync(projectId, "fp-triage", "TypeError: boom");

        var body = await CaptureAsync(receiver, async () =>
        {
            var patch = await client.PatchAsJsonAsync(
                $"/api/projects/{projectId}/issues/{issueId}", new { status = 2 });
            Assert.Equal(HttpStatusCode.NoContent, patch.StatusCode);
        });

        using var doc = JsonDocument.Parse(body);
        Assert.Equal("resolved", doc.RootElement.GetProperty("trigger").GetString());
        Assert.Equal("TypeError: boom", doc.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Resolving_over_MCP_fires_the_same_Resolved_rule()
    {
        var (client, projectId) = await ProvisionAsync();
        using var receiver = LoopbackHttpReceiver.Start();
        await AddResolvedRuleAsync(client, projectId, receiver.Url);
        var issueId = await SeedErrorIssueAsync(projectId, "fp-mcp-triage", "TypeError: agent boom");

        var mint = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/mcp-tokens", new { name = "Claude", capability = "triage" });
        var raw = (await mint.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        var agent = ControlPlaneApp.Create(pg.ConnectionString).CreateClient();
        agent.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", raw);

        var body = await CaptureAsync(receiver, async () =>
        {
            var resp = await agent.PostAsJsonAsync("/api/mcp", new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "tools/call",
                @params = new
                {
                    name = "set_issue_status",
                    arguments = new { issueId = issueId.ToString(), status = "resolved" },
                },
            });
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var rpc = await resp.Content.ReadFromJsonAsync<JsonElement>();
            Assert.False(rpc.GetProperty("result").GetProperty("isError").GetBoolean());
        });

        using var doc = JsonDocument.Parse(body);
        Assert.Equal("resolved", doc.RootElement.GetProperty("trigger").GetString());
        Assert.Equal("TypeError: agent boom", doc.RootElement.GetProperty("title").GetString());
    }
}
