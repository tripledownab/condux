using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Condux.Core.Events;
using Condux.Core.Grouping;
using Condux.Core.Plans;
using Condux.IntegrationTests.Fixtures;
using Condux.Storage.ClickHouse;
using Condux.Storage.Postgres;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// HTTP-level test of the issue-detail endpoint (#36) with tenancy enforced (#48): hosts the real
/// control-plane against an ephemeral Postgres (grouped issue) and ClickHouse (raw events).
/// Provisions a real org+project via the authenticated API — issues are keyed by the project's
/// numeric id, exactly as the relay writes them — then asserts the owner can read the stitched
/// detail and 404s on a missing issue.
/// </summary>
[Trait("Category", "Integration")]
public sealed class IssueDetailApiTest(PostgresFixture pg, ClickHouseFixture ch)
    : IClassFixture<PostgresFixture>, IClassFixture<ClickHouseFixture>
{
    private HttpClient CreateClient() => ControlPlaneApp.Create(pg.ConnectionString, ch).CreateClient();

    // Sign up, create an org + project, and return the (authenticated client, numeric projectId).
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

    [Fact]
    public async Task Detail_ReturnsIssueWithRecentEvents()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var (client, projectId) = await ProvisionAsync();
        var project = projectId.ToString();
        var grouping = new Grouping("fp-detail", "ValueError: boom", "run");

        // Seed a grouped issue (2 occurrences) in Postgres (bigint project id); ClickHouse + the URL
        // below use the string form (`project`), which is the same numeric value at the wire boundary.
        var repo = new IssueRepository(pg.ConnectionString);
        var r1 = await repo.UpsertAsync(projectId, grouping, Level.Error, DateTimeOffset.UtcNow);
        await repo.UpsertAsync(projectId, grouping, Level.Error, DateTimeOffset.UtcNow);

        // Seed the raw event in ClickHouse (event_id is FixedString(32) → 32-char id).
        using var httpClient = new HttpClient();
        ClickHouseRegistration.Configure(httpClient, ch.BaseUrl, ch.ChUser, ch.ChPassword);
        var writer = new ClickHouseEventWriter(httpClient);
        var e = new Event
        {
            EventId = Guid.NewGuid().ToString("N"),
            Level = Level.Error,
            Message = "boom",
            TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        await writer.InsertAsync([ClickHouseEventWriter.ToRow(
            project, (ulong)r1.Id, e, grouping.Fingerprint, PlanCatalog.For(Tier.Free).RetentionDays)]);

        // The API exposes the public UUID, not the internal bigint; read it from the list endpoint.
        var list = (await client.GetFromJsonAsync<JsonElement>($"/api/projects/{project}/issues"))
            .GetProperty("issues");
        var publicId = list[0].GetProperty("id").GetString();
        Assert.True(Guid.TryParse(publicId, out _));

        var detail = await client.GetFromJsonAsync<JsonElement>(
            $"/api/projects/{project}/issues/{publicId}");

        var issue = detail.GetProperty("issue");
        Assert.Equal(publicId, issue.GetProperty("id").GetString());
        Assert.Equal("ValueError: boom", issue.GetProperty("title").GetString());
        Assert.Equal(2L, issue.GetProperty("eventCount").GetInt64());

        var events = detail.GetProperty("events");
        Assert.True(events.GetArrayLength() >= 1);
        Assert.Equal("boom", events[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task Detail_UnknownIssue_Returns404()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var (client, projectId) = await ProvisionAsync();
        var resp = await client.GetAsync($"/api/projects/{projectId}/issues/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
