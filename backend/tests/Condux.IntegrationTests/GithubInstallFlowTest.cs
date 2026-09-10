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
/// HTTP-level test of the two routes GitHub itself drives (#61): the Setup URL it sends a browser to after
/// an install, and the webhook (HMAC-verified) that removes an installation on uninstall. Gated by the
/// opt-in GitHub config, which is set here for the test app. Connecting is in GithubOauthConnectTest.
/// </summary>
[Trait("Category", "Integration")]
public sealed class GithubInstallFlowTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private const string WebhookSecret = GithubAppSettings.WebhookSecret;
    private const string StateCookie = "condux_github_state";

    private WebApplicationFactory<Program> CreateApp() =>
        ControlPlaneApp.Create(pg.ConnectionString).WithWebHostBuilder(GithubAppSettings.Apply);

    /// <summary>
    /// The heart of it: an installation id in the query is not evidence of anything, so this leg writes
    /// nothing at all. It hands the browser to user authorization, whose callback asks GitHub which
    /// installations that person can actually reach.
    /// </summary>
    [Fact]
    public async Task Setup_sends_the_browser_to_user_authorization_and_links_nothing()
    {
        var app = CreateApp();
        var client = app.CreateClient();
        await ApiAuth.SignUpAsync(client);
        var orgId = await CreateOrgAsync(client);

        var state = GithubConnectState.Create(
            orgId, "/onboarding", false, DateTimeOffset.UtcNow.AddMinutes(10), WebhookSecret);

        var landed = await SetupAsync(app, 555, state, state);
        Assert.Equal(HttpStatusCode.Redirect, landed.StatusCode);
        Assert.StartsWith(
            "https://github.com/login/oauth/authorize?", landed.Headers.Location!.ToString());
        Assert.Empty(await new GithubInstallationRepository(pg.ConnectionString).GetByOrgAsync(orgId));

        // The onward state says an install just happened, so the callback can tell an installation still
        // awaiting an owner's approval from one that was never installed.
        var onward = Uri.UnescapeDataString(landed.Headers.Location!.ToString().Split("state=")[1]);
        var verified = GithubConnectState.Validate(onward, DateTimeOffset.UtcNow, WebhookSecret);
        Assert.True(verified!.Installed);
        Assert.Equal(orgId, verified.OrgId);
        Assert.Equal("/onboarding", verified.ReturnPath);
    }

    [Fact]
    public async Task Setup_refuses_a_forged_state_or_one_this_browser_never_started()
    {
        var app = CreateApp();
        var client = app.CreateClient();
        await ApiAuth.SignUpAsync(client);
        var orgId = await CreateOrgAsync(client);
        var browser = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var bad = await browser.GetAsync("/api/github/setup?installation_id=777&state=garbage");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        // A real state without the cookie the connect leg set is refused too: the state proves only which
        // org minted it, so on its own it would let anyone finish someone else's install.
        var state = GithubConnectState.Create(
            orgId, "/onboarding", false, DateTimeOffset.UtcNow.AddMinutes(10), WebhookSecret);
        var uncookied = await SetupAsync(app, 778, state, cookieState: null);
        Assert.Equal(HttpStatusCode.BadRequest, uncookied.StatusCode);
    }

    /// <summary>
    /// The tenancy guard lives in the link statement rather than in any caller, so it is tested there.
    /// Reaching an installation on GitHub takes only read access, so a caller who can name an id must not
    /// be able to take it from the org using it.
    /// </summary>
    [Fact]
    public async Task LinkAsync_refuses_an_installation_another_org_holds()
    {
        var app = CreateApp();
        var repo = new GithubInstallationRepository(pg.ConnectionString);

        var victim = app.CreateClient();
        await ApiAuth.SignUpAsync(victim);
        var victimOrgId = await CreateOrgAsync(victim);
        Assert.True(await repo.LinkAsync(600, victimOrgId, "victim-co"));

        var other = app.CreateClient();
        await ApiAuth.SignUpAsync(other);
        var otherOrgId = await CreateOrgAsync(other);

        Assert.False(await repo.LinkAsync(600, otherOrgId, "other-co"));

        // The victim keeps it, account login included.
        var held = await repo.GetAsync(600);
        Assert.Equal(victimOrgId, held!.OrgId);
        Assert.Equal("victim-co", held.AccountLogin);
        Assert.Empty(await repo.GetByOrgAsync(otherOrgId));

        // The same org relinking is fine, and may fill in a login it did not have before.
        Assert.True(await repo.LinkAsync(600, victimOrgId, "victim-co"));
    }

    private static async Task<HttpResponseMessage> SetupAsync(
        WebApplicationFactory<Program> app, long installationId, string state, string? cookieState)
    {
        var browser = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/github/setup?installation_id={installationId}&state={Uri.EscapeDataString(state)}");
        if (cookieState is not null)
        {
            request.Headers.Add("Cookie", $"{StateCookie}={cookieState}");
        }
        return await browser.SendAsync(request);
    }

    [Fact]
    public async Task Webhook_verifies_the_signature_and_removes_an_uninstalled_installation()
    {
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
