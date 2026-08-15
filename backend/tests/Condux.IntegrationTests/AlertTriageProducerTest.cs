using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Condux.Core.Events;
using Condux.Core.Grouping;
using Condux.IntegrationTests.Fixtures;
using Condux.Storage.Postgres;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// The triage-event producers (alert events): manually resolving an issue through the control-plane PATCH
/// fires a rule that subscribes to the Resolved event, delivered over a real socket to a webhook. Proves
/// the control-plane (not just the ingest consumer) dispatches alerts. Assigning is the sibling path,
/// sharing the same best-effort helper.
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

    [Fact]
    public async Task Resolving_an_issue_fires_a_Resolved_rule_to_a_real_webhook()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var (client, projectId) = await ProvisionAsync();

        // A free loopback port for the webhook receiver.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var url = $"http://127.0.0.1:{port}/hook/";

        // A rule that fires on the Resolved event (3) at Error (4), delivering to the webhook.
        var createResp = await client.PostAsJsonAsync($"/api/projects/{projectId}/alert-rules",
            new { name = "resolutions", events = new[] { 3 }, levels = new[] { 4 } });
        var ruleId = (await createResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        await client.PostAsJsonAsync($"/api/projects/{projectId}/alert-rules/{ruleId}/channels",
            new { channel = 3, target = url });

        // Seed an Error issue directly (issues come from ingest, not an API); grab its public id.
        var issues = new IssueRepository(pg.ConnectionString);
        var upsert = await issues.UpsertAsync(
            projectId, new Grouping("fp-triage", "TypeError: boom", "app.py:1"), Level.Error, DateTimeOffset.UtcNow);

        // The receiver captures the one POST the Resolved producer sends.
        using var listener = new HttpListener();
        listener.Prefixes.Add(url);
        listener.Start();
        var body = "";
        var serverTask = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            using var reader = new StreamReader(context.Request.InputStream);
            body = await reader.ReadToEndAsync();
            context.Response.StatusCode = 200;
            context.Response.Close();
        });

        // Manually resolve the issue (status 2) via the control-plane triage endpoint.
        var patch = await client.PatchAsJsonAsync(
            $"/api/projects/{projectId}/issues/{upsert.PublicId}", new { status = 2 });
        Assert.Equal(HttpStatusCode.NoContent, patch.StatusCode);

        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        listener.Stop();

        using var doc = JsonDocument.Parse(body);
        Assert.Equal("resolved", doc.RootElement.GetProperty("trigger").GetString());
        Assert.Equal("TypeError: boom", doc.RootElement.GetProperty("title").GetString());
    }
}
