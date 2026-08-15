using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Condux.GitHub;
using Condux.IntegrationTests.Fixtures;
using Condux.Storage.Postgres;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// HTTP-level test of the GitHub App install flow (#61): connect mints a signed state + install URL, the
/// Setup URL ties an installation to an org via that state, and the webhook (HMAC-verified) removes an
/// installation on uninstall. Gated by the opt-in GitHub config, which is set here for the test app.
/// </summary>
[Trait("Category", "Integration")]
public sealed class GithubInstallFlowTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private const string WebhookSecret = "test-webhook-secret";
    private const string Slug = "condux-test";
    private static readonly string PrivateKeyPem = RSA.Create(2048).ExportRSAPrivateKeyPem();

    private WebApplicationFactory<Program> CreateApp() =>
        ControlPlaneApp.Create(pg.ConnectionString).WithWebHostBuilder(b => b
            .UseSetting("CONDUX_GITHUB_CLIENT_ID", "Iv1.test")
            .UseSetting("CONDUX_GITHUB_WEBHOOK_SECRET", WebhookSecret)
            .UseSetting("CONDUX_GITHUB_APP_SLUG", Slug)
            .UseSetting("CONDUX_GITHUB_PRIVATE_KEY", PrivateKeyPem));

    [Fact]
    public async Task Connect_returns_an_install_url_carrying_a_valid_state()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var client = CreateApp().CreateClient();
        await ApiAuth.SignUpAsync(client);
        var orgId = await CreateOrgAsync(client);

        var resp = await client.PostAsJsonAsync($"/api/orgs/{orgId}/github/connect",
            new { returnPath = "/projects/proj-uuid" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var url = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("installUrl").GetString()!;
        Assert.Contains($"github.com/apps/{Slug}/installations/new", url);
        var state = Uri.UnescapeDataString(url.Split("state=")[1]);
        // The state carries both the org to tie the install to and the in-app path to return to.
        var verified = GithubConnectState.Validate(state, DateTimeOffset.UtcNow, WebhookSecret);
        Assert.NotNull(verified);
        Assert.Equal(orgId, verified!.OrgId);
        Assert.Equal("/projects/proj-uuid", verified.ReturnPath);
    }

    [Fact]
    public async Task Setup_links_the_installation_to_the_org_for_a_valid_state_and_rejects_a_bad_one()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var app = CreateApp();
        var client = app.CreateClient();
        await ApiAuth.SignUpAsync(client);
        var orgId = await CreateOrgAsync(client);

        var state = GithubConnectState.Create(
            orgId, "/onboarding", DateTimeOffset.UtcNow.AddMinutes(10), WebhookSecret);
        var browser = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var ok = await browser.GetAsync($"/api/github/setup?installation_id=555&state={Uri.EscapeDataString(state)}");
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        // The browser lands back where the connect started, flagged so the page shows a connected banner.
        Assert.Contains("/onboarding?github=connected", ok.Headers.Location!.ToString());
        var installs = await new GithubInstallationRepository(pg.ConnectionString).GetByOrgAsync(orgId);
        Assert.Contains(installs, i => i.InstallationId == 555);

        // The settings tab reads the linked installs back, so the connected state survives a revisit.
        var listed = await client.GetFromJsonAsync<JsonElement>($"/api/orgs/{orgId}/github");
        Assert.Contains(
            listed.EnumerateArray(),
            i => i.GetProperty("installationId").GetInt64() == 555);

        var bad = await browser.GetAsync("/api/github/setup?installation_id=777&state=garbage");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Webhook_verifies_the_signature_and_removes_an_uninstalled_installation()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var app = CreateApp();
        var client = app.CreateClient();
        await ApiAuth.SignUpAsync(client);
        var orgId = await CreateOrgAsync(client);

        var repo = new GithubInstallationRepository(pg.ConnectionString);
        await repo.LinkAsync(999, orgId);
        var payload = Encoding.UTF8.GetBytes("""{"action":"deleted","installation":{"id":999}}""");

        var ok = await SendWebhookAsync(app, payload, Sign(WebhookSecret, payload));
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);
        Assert.DoesNotContain(await repo.GetByOrgAsync(orgId), i => i.InstallationId == 999);

        // A wrong signature is rejected (and never acts).
        await repo.LinkAsync(1000, orgId);
        var bad = await SendWebhookAsync(app, payload, "sha256=deadbeef");
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
        Assert.Contains(await repo.GetByOrgAsync(orgId), i => i.InstallationId == 1000);
    }

    private static async Task<HttpResponseMessage> SendWebhookAsync(
        WebApplicationFactory<Program> app, byte[] body, string signature)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/github/webhook") { Content = new ByteArrayContent(body) };
        req.Headers.Add("X-GitHub-Event", "installation");
        req.Headers.Add("X-Hub-Signature-256", signature);
        return await app.CreateClient().SendAsync(req);
    }

    private static string Sign(string secret, byte[] body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return "sha256=" + Convert.ToHexStringLower(hmac.ComputeHash(body));
    }

    private static async Task<long> CreateOrgAsync(HttpClient client)
    {
        var resp = await client.PostAsJsonAsync("/api/orgs",
            new { slug = "org-" + Guid.NewGuid().ToString("N"), name = "Org" });
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
    }
}
