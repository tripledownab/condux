using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Condux.IntegrationTests.Fixtures;
using Condux.Storage.Postgres;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// What an org reads back through its GitHub installation, and who is allowed to. The tenancy filter is
/// the part worth pinning: it is an endpoint filter, so dropping one changes no signature, produces no
/// compiler error, and leaves the exported OpenAPI document byte-identical. These four routes were moved
/// to their own file with exactly that risk, and at the time nothing exercised them over HTTP at all.
///
/// The repositories and branches routes call GitHub once an installation exists, so the cases here are
/// the ones that answer before reaching it: who may ask, and what an org with nothing linked is told.
/// </summary>
[Trait("Category", "Integration")]
public sealed class GithubInstallationApiTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private WebApplicationFactory<Program> CreateApp() =>
        ControlPlaneApp.Create(pg.ConnectionString).WithWebHostBuilder(GithubAppSettings.Apply);

    [Fact]
    public async Task A_member_reads_the_orgs_linked_installations()
    {
        var app = CreateApp();
        var client = app.CreateClient();
        await ApiAuth.SignUpAsync(client);
        var orgId = await CreateOrgAsync(client);
        Assert.True(await new GithubInstallationRepository(pg.ConnectionString)
            .LinkAsync(310, orgId, "acme"));

        var listed = await client.GetFromJsonAsync<JsonElement>($"/api/orgs/{orgId}/github");

        var installation = Assert.Single(listed.EnumerateArray());
        Assert.Equal(310, installation.GetProperty("installationId").GetInt64());
        Assert.Equal("acme", installation.GetProperty("accountLogin").GetString());
        // The manage link is built from the app slug, so the dashboard can send someone to GitHub without
        // a second round trip.
        Assert.Contains(GithubAppSettings.Slug, installation.GetProperty("manageUrl").GetString());
    }

    /// <summary>
    /// A non-member gets 404 rather than 403, so the API does not confirm that an org id exists. An
    /// installation list names a GitHub account, which is exactly the sort of thing that must not leak
    /// across tenants.
    /// </summary>
    [Fact]
    public async Task A_non_member_is_told_the_org_does_not_exist()
    {
        var app = CreateApp();
        var owner = app.CreateClient();
        await ApiAuth.SignUpAsync(owner);
        var orgId = await CreateOrgAsync(owner);
        Assert.True(await new GithubInstallationRepository(pg.ConnectionString)
            .LinkAsync(311, orgId, "victim-co"));

        var outsider = app.CreateClient();
        await ApiAuth.SignUpAsync(outsider);

        foreach (var route in new[] { "", "/health", "/repositories", "/branches?repo=acme/api" })
        {
            var resp = await outsider.GetAsync($"/api/orgs/{orgId}/github{route}");
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        }
    }

    [Fact]
    public async Task An_anonymous_caller_is_refused()
    {
        var app = CreateApp();
        var owner = app.CreateClient();
        await ApiAuth.SignUpAsync(owner);
        var orgId = await CreateOrgAsync(owner);

        var anonymous = app.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        foreach (var route in new[] { "", "/health", "/repositories", "/branches?repo=acme/api" })
        {
            var resp = await anonymous.GetAsync($"/api/orgs/{orgId}/github{route}");
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        }
    }

    /// <summary>
    /// Nothing linked is a reconnect prompt, not an error: GitHub may still hold an installation whose row
    /// we lost, which is the case the connect flow exists to recover. It answers without calling GitHub.
    /// </summary>
    [Fact]
    public async Task Health_reports_not_connected_when_the_org_has_nothing_linked()
    {
        var client = CreateApp().CreateClient();
        await ApiAuth.SignUpAsync(client);
        var orgId = await CreateOrgAsync(client);

        var health = await client.GetFromJsonAsync<JsonElement>($"/api/orgs/{orgId}/github/health");

        Assert.Equal("not_connected", health.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, health.GetProperty("installationId").ValueKind);
    }

    private static async Task<long> CreateOrgAsync(HttpClient client)
    {
        var resp = await client.PostAsJsonAsync("/api/orgs",
            new { slug = "org-" + Guid.NewGuid().ToString("N"), name = "Org" });
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
    }
}
