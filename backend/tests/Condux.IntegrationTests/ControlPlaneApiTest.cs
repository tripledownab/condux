using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Condux.IntegrationTests.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// HTTP-level test of the control-plane provisioning API (#34): hosts the real
/// app via WebApplicationFactory against an ephemeral Postgres, exercising routing,
/// JSON binding, DI, and the repositories end to end.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ControlPlaneApiTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private HttpClient CreateClient() => ControlPlaneApp.Create(pg.ConnectionString).CreateClient();

    [Fact]
    public async Task ProvisionOrgProjectAndKey_OverHttp()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var client = CreateClient();
        await ApiAuth.SignUpAsync(client); // authenticate; the caller owns any org it creates

        // Create an org (tier 2 = Business).
        var orgResp = await client.PostAsJsonAsync("/api/orgs",
            new { slug = "acme-" + Guid.NewGuid().ToString("N"), name = "Acme", tier = 2 });
        Assert.Equal(HttpStatusCode.Created, orgResp.StatusCode);
        var org = await orgResp.Content.ReadFromJsonAsync<JsonElement>();
        var orgId = org.GetProperty("id").GetInt64();

        // Create a project → returns the project, its first key, and a usable DSN.
        var projResp = await client.PostAsJsonAsync($"/api/orgs/{orgId}/projects",
            new { slug = "backend", name = "Backend", platform = "python" });
        Assert.Equal(HttpStatusCode.Created, projResp.StatusCode);
        var created = await projResp.Content.ReadFromJsonAsync<JsonElement>();
        var projectId = created.GetProperty("project").GetProperty("id").GetInt64();
        var dsn = created.GetProperty("dsn").GetString();
        var firstKey = created.GetProperty("key").GetProperty("publicKey").GetString();
        Assert.False(string.IsNullOrEmpty(firstKey));
        Assert.Contains(firstKey!, dsn!);
        // #126: the DSN carries the project's public UUID, not the numeric bigint id.
        var publicId = created.GetProperty("project").GetProperty("publicId").GetString();
        Assert.Contains($"/{publicId}", dsn!);

        // The project is listed under its org.
        var list = await client.GetFromJsonAsync<JsonElement>($"/api/orgs/{orgId}/projects");
        Assert.Contains(list.EnumerateArray(), p => p.GetProperty("id").GetInt64() == projectId);

        // Mint a second key, then revoke it.
        var keyResp = await client.PostAsJsonAsync($"/api/projects/{projectId}/keys", new { label = "ci" });
        Assert.Equal(HttpStatusCode.Created, keyResp.StatusCode);
        var key = await keyResp.Content.ReadFromJsonAsync<JsonElement>();
        var keyId = key.GetProperty("key").GetProperty("id").GetInt64();

        var revoke = await client.PostAsync($"/api/projects/{projectId}/keys/{keyId}/revoke", content: null);
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);

        // Two keys exist; exactly one (the default) is still active.
        var keys = await client.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}/keys");
        Assert.Equal(2, keys.GetArrayLength());
        var active = keys.EnumerateArray().Count(k => k.GetProperty("isActive").GetBoolean());
        Assert.Equal(1, active);
    }

    [Fact]
    public async Task Unknown_Org_And_Project_Return404()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var client = CreateClient();
        await ApiAuth.SignUpAsync(client);

        // A non-member (which the caller is, for an org it didn't create) gets 404 — existence hidden.
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/orgs/999999")).StatusCode);
        var resp = await client.PostAsJsonAsync("/api/orgs/999999/projects",
            new { slug = "x", name = "X", platform = "go" });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Provisioning_Requires_Authentication()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var anon = CreateClient(); // no signup → no session cookie

        var resp = await anon.PostAsJsonAsync("/api/orgs", new { slug = "x", name = "X", tier = 0 });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/orgs")).StatusCode);
    }
}
