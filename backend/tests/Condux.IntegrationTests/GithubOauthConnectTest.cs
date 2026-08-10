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
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// Connecting through user authorization, which is what makes a lost link recoverable: the install URL
/// stops redirecting once the app is installed, so an org whose row disappeared could never get it back.
/// Covers the three shapes the callback has to handle (one installation, none, several) and the tenancy
/// guard on the pick, against a stubbed GitHub.
/// </summary>
[Trait("Category", "Integration")]
public sealed class GithubOauthConnectTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private const string WebhookSecret = "test-webhook-secret";
    private const string Slug = "condux-test";
    private const string RedirectUri = "https://app.condux.test/api/github/oauth/callback";
    private static readonly string PrivateKeyPem = RSA.Create(2048).ExportRSAPrivateKeyPem();

    // A stub GitHub: the token endpoint always hands back a user token, and /user/installations returns
    // whatever the test set up. Routed by path so one handler serves both legs.
    private sealed class StubGithub(string installationsJson) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.RequestUri!.AbsolutePath.EndsWith("/user/installations", StringComparison.Ordinal)
                ? installationsJson
                : """{"access_token":"ghu_test","token_type":"bearer"}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private WebApplicationFactory<Program> CreateApp(string installationsJson) =>
        ControlPlaneApp.Create(pg.ConnectionString).WithWebHostBuilder(b =>
        {
            b.UseSetting("CONDUX_GITHUB_CLIENT_ID", "Iv1.test");
            b.UseSetting("CONDUX_GITHUB_WEBHOOK_SECRET", WebhookSecret);
            b.UseSetting("CONDUX_GITHUB_APP_SLUG", Slug);
            b.UseSetting("CONDUX_GITHUB_PRIVATE_KEY", PrivateKeyPem);
            b.UseSetting("CONDUX_GITHUB_CLIENT_SECRET", "client-secret");
            b.UseSetting("CONDUX_GITHUB_OAUTH_REDIRECT_URI", RedirectUri);
            b.UseSetting("CONDUX_APP_BASE_URL", "https://app.condux.test");
            b.ConfigureTestServices(s => s
                .AddHttpClient("GitHubUserOAuth")
                .ConfigurePrimaryHttpMessageHandler(() => new StubGithub(installationsJson)));
        });

    [Fact]
    public async Task Connect_sends_the_browser_to_authorize_not_install_when_user_oauth_is_configured()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var client = CreateApp(NoInstallations).CreateClient();
        await ApiAuth.SignUpAsync(client);
        var orgId = await CreateOrgAsync(client);

        var resp = await client.PostAsJsonAsync($"/api/orgs/{orgId}/github/connect",
            new { returnPath = "/projects/proj-uuid" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var url = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("installUrl").GetString()!;
        // Authorize, not installations/new: this leg comes back to us even when the app is already installed.
        Assert.StartsWith("https://github.com/login/oauth/authorize?", url);
        Assert.Contains($"redirect_uri={Uri.EscapeDataString(RedirectUri)}", url);
    }

    // The bug this exists for: GitHub still holds the installation, our row is gone, and the user has no
    // way back. Authorizing finds the installation they can already reach and re-links it.
    [Fact]
    public async Task Callback_relinks_the_single_installation_the_user_can_reach()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var app = CreateApp(Installations((41, "acme")));
        var client = app.CreateClient();
        await ApiAuth.SignUpAsync(client);
        var orgId = await CreateOrgAsync(client);

        var redirect = await CallbackAsync(app, orgId, "/projects/proj-uuid");

        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        Assert.Equal(
            "https://app.condux.test/projects/proj-uuid?github=connected",
            redirect.Headers.Location!.ToString());
        var linked = await new GithubInstallationRepository(pg.ConnectionString).GetByOrgAsync(orgId);
        // The account login rides along from the installations read, so the UI can name it without a webhook.
        Assert.Contains(linked, i => i.InstallationId == 41 && i.AccountLogin == "acme");
    }

    [Fact]
    public async Task Callback_sends_a_user_who_authorized_but_never_installed_on_to_install()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var app = CreateApp(NoInstallations);
        var client = app.CreateClient();
        await ApiAuth.SignUpAsync(client);
        var orgId = await CreateOrgAsync(client);

        var redirect = await CallbackAsync(app, orgId, "/onboarding");

        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        var location = redirect.Headers.Location!.ToString();
        Assert.Contains($"github.com/apps/{Slug}/installations/new", location);
        // The onward state still carries the org, so the Setup URL can finish the link.
        var state = Uri.UnescapeDataString(location.Split("state=")[1]);
        Assert.Equal(orgId, GithubConnectState.Validate(state, DateTimeOffset.UtcNow, WebhookSecret)!.OrgId);
    }

    [Fact]
    public async Task Callback_hands_back_a_signed_choice_when_several_installations_are_reachable()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var app = CreateApp(Installations((51, "acme"), (52, "acme-labs")));
        var client = app.CreateClient();
        await ApiAuth.SignUpAsync(client);
        var orgId = await CreateOrgAsync(client);

        var redirect = await CallbackAsync(app, orgId, "/projects/proj-uuid");
        var location = redirect.Headers.Location!.ToString();
        Assert.Contains("github=select", location);
        var selection = Uri.UnescapeDataString(location.Split("selection=")[1]);

        // Nothing is linked until the user picks.
        var repo = new GithubInstallationRepository(pg.ConnectionString);
        Assert.Empty(await repo.GetByOrgAsync(orgId));

        var options = await client.GetFromJsonAsync<JsonElement>(
            $"/api/orgs/{orgId}/github/selection?token={Uri.EscapeDataString(selection)}");
        Assert.Equal(
            ["acme", "acme-labs"],
            options.GetProperty("installations").EnumerateArray()
                .Select(i => i.GetProperty("accountLogin").GetString()));

        var picked = await client.PostAsJsonAsync($"/api/orgs/{orgId}/github/select",
            new { selection, installationId = 52 });
        Assert.Equal(HttpStatusCode.NoContent, picked.StatusCode);
        Assert.Contains(await repo.GetByOrgAsync(orgId), i => i.InstallationId == 52);
    }

    // The ids are signed precisely so the browser cannot name one of its own: reaching an installation on
    // GitHub takes only read access, so an unsigned pick would be a way to grab another tenant's repos.
    [Fact]
    public async Task Select_refuses_an_installation_that_was_not_in_the_signed_choice()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var app = CreateApp(Installations((61, "acme"), (62, "acme-labs")));
        var client = app.CreateClient();
        await ApiAuth.SignUpAsync(client);
        var orgId = await CreateOrgAsync(client);

        var location = (await CallbackAsync(app, orgId, "/")).Headers.Location!.ToString();
        var selection = Uri.UnescapeDataString(location.Split("selection=")[1]);

        var forged = await client.PostAsJsonAsync($"/api/orgs/{orgId}/github/select",
            new { selection, installationId = 999 });
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);

        var tampered = await client.PostAsJsonAsync($"/api/orgs/{orgId}/github/select",
            new { selection = selection + "x", installationId = 61 });
        Assert.Equal(HttpStatusCode.BadRequest, tampered.StatusCode);

        Assert.Empty(await new GithubInstallationRepository(pg.ConnectionString).GetByOrgAsync(orgId));
    }

    [Fact]
    public async Task Callback_leaves_an_installation_that_belongs_to_another_org_alone()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var app = CreateApp(Installations((71, "acme")));
        var client = app.CreateClient();
        await ApiAuth.SignUpAsync(client);
        var orgId = await CreateOrgAsync(client);

        // Another tenant already owns this installation.
        var repo = new GithubInstallationRepository(pg.ConnectionString);
        var otherOrgId = await CreateOrgAsync(app.CreateClient(), signUp: true);
        await repo.LinkAsync(71, otherOrgId);

        var redirect = await CallbackAsync(app, orgId, "/projects/proj-uuid");
        Assert.Contains("github=taken", redirect.Headers.Location!.ToString());
        Assert.Equal(otherOrgId, (await repo.GetAsync(71))!.OrgId);
    }

    // Disconnect is what gives the cross-tenant guard a way out: an org that no longer holds an
    // installation frees it for another to claim, which is the only way to move one between orgs.
    [Fact]
    public async Task Disconnect_frees_the_installation_for_another_org_to_claim()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var app = CreateApp(Installations((81, "acme")));
        var repo = new GithubInstallationRepository(pg.ConnectionString);

        var first = app.CreateClient();
        await ApiAuth.SignUpAsync(first);
        var firstOrgId = await CreateOrgAsync(first);
        await CallbackAsync(app, firstOrgId, "/");
        Assert.Contains(await repo.GetByOrgAsync(firstOrgId), i => i.InstallationId == 81);

        // A second org cannot take it while the first still holds it.
        var second = app.CreateClient();
        var secondOrgId = await CreateOrgAsync(second, signUp: true);
        Assert.Contains("github=taken", (await CallbackAsync(app, secondOrgId, "/")).Headers.Location!.ToString());

        var disconnected = await first.DeleteAsync($"/api/orgs/{firstOrgId}/github");
        Assert.Equal(HttpStatusCode.NoContent, disconnected.StatusCode);
        Assert.Empty(await repo.GetByOrgAsync(firstOrgId));

        // Now it is free, so the second org connects normally.
        Assert.Contains(
            "github=connected", (await CallbackAsync(app, secondOrgId, "/")).Headers.Location!.ToString());
        Assert.Contains(await repo.GetByOrgAsync(secondOrgId), i => i.InstallationId == 81);
    }

    // Disconnecting only unlinks, so the same org can pick it straight back up. That reversibility is
    // why this does not uninstall on GitHub.
    [Fact]
    public async Task Disconnect_is_reversible_and_404s_when_there_is_nothing_linked()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var app = CreateApp(Installations((82, "acme")));
        var client = app.CreateClient();
        await ApiAuth.SignUpAsync(client);
        var orgId = await CreateOrgAsync(client);

        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/orgs/{orgId}/github")).StatusCode);

        await CallbackAsync(app, orgId, "/");
        Assert.Equal(
            HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/orgs/{orgId}/github")).StatusCode);

        await CallbackAsync(app, orgId, "/");
        Assert.Contains(
            await new GithubInstallationRepository(pg.ConnectionString).GetByOrgAsync(orgId),
            i => i.InstallationId == 82);
    }

    [Fact]
    public async Task Callback_returns_the_user_to_the_app_when_the_state_is_missing_or_forged()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var browser = CreateApp(NoInstallations)
            .CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var forged = await browser.GetAsync("/api/github/oauth/callback?code=abc&state=garbage");
        Assert.Equal(HttpStatusCode.Redirect, forged.StatusCode);
        Assert.Equal("https://app.condux.test/?github=failed", forged.Headers.Location!.ToString());
    }

    private static async Task<HttpResponseMessage> CallbackAsync(
        WebApplicationFactory<Program> app, long orgId, string returnPath)
    {
        var state = GithubConnectState.Create(
            orgId, returnPath, DateTimeOffset.UtcNow.AddMinutes(10), WebhookSecret);
        var browser = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        return await browser.GetAsync(
            $"/api/github/oauth/callback?code=the-code&state={Uri.EscapeDataString(state)}");
    }

    private const string NoInstallations = """{"total_count":0,"installations":[]}""";

    private static string Installations(params (long Id, string Login)[] installations)
    {
        var items = installations.Select(i => JsonSerializer.Serialize(new
        {
            id = i.Id,
            app_slug = Slug,
            account = new { login = i.Login },
        }));
        return JsonSerializer.Serialize(new
        {
            total_count = installations.Length,
            installations = JsonSerializer.Deserialize<JsonElement>($"[{string.Join(",", items)}]"),
        });
    }

    private static async Task<long> CreateOrgAsync(HttpClient client, bool signUp = false)
    {
        if (signUp)
        {
            await ApiAuth.SignUpAsync(client);
        }

        var resp = await client.PostAsJsonAsync("/api/orgs",
            new { slug = "org-" + Guid.NewGuid().ToString("N"), name = "Org", tier = 0 });
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
    }
}
