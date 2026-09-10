using System.Net;
using System.Net.Http.Json;
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
    private const string WebhookSecret = GithubAppSettings.WebhookSecret;
    private const string Slug = GithubAppSettings.Slug;
    private const string RedirectUri = GithubAppSettings.RedirectUri;
    private const string StateCookie = "condux_github_state";

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
        ControlPlaneApp.Create(pg.ConnectionString, appBaseUrl: "https://app.condux.test")
            .WithWebHostBuilder(b =>
        {
            GithubAppSettings.Apply(b);
            b.ConfigureTestServices(s => s
                .AddHttpClient("GitHubUserOAuth")
                .ConfigurePrimaryHttpMessageHandler(() => new StubGithub(installationsJson)));
        });

    [Fact]
    public async Task Connect_sends_the_browser_to_authorize_not_install_when_user_oauth_is_configured()
    {
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

        // The browser's half of the double submit: the state it will hand back is remembered in a cookie,
        // so the return leg can tell this browser from one that was merely sent the link.
        var state = Uri.UnescapeDataString(url.Split("state=")[1]);
        Assert.Contains(
            resp.Headers.GetValues("Set-Cookie"),
            c => c.StartsWith($"{StateCookie}={state};", StringComparison.Ordinal));
    }

    // The bug this exists for: GitHub still holds the installation, our row is gone, and the user has no
    // way back. Authorizing finds the installation they can already reach and re-links it.
    [Fact]
    public async Task Callback_relinks_the_single_installation_the_user_can_reach()
    {
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
        var app = CreateApp(NoInstallations);
        var client = app.CreateClient();
        await ApiAuth.SignUpAsync(client);
        var orgId = await CreateOrgAsync(client);

        var redirect = await CallbackAsync(app, orgId, "/onboarding");

        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        var location = redirect.Headers.Location!.ToString();
        Assert.Contains($"github.com/apps/{Slug}/installations/new", location);
        // The onward state still carries the org, so the Setup URL can carry the flow on.
        var state = Uri.UnescapeDataString(location.Split("state=")[1]);
        Assert.Equal(orgId, GithubConnectState.Validate(state, DateTimeOffset.UtcNow, WebhookSecret)!.OrgId);

        // The browser's half has to come with it. This leg consumes the cookie it arrived with and then
        // sets a new one, so the response carries both a deletion and the replacement; the last wins, and
        // if it did not, every install started from here would fail at the Setup URL instead.
        var cookies = redirect.Headers.GetValues("Set-Cookie")
            .Where(c => c.StartsWith($"{StateCookie}=", StringComparison.Ordinal)).ToList();
        Assert.Equal($"{StateCookie}={state}", cookies[^1].Split(';')[0]);
    }

    [Fact]
    public async Task Callback_hands_back_a_signed_choice_when_several_installations_are_reachable()
    {
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
        var browser = CreateApp(NoInstallations)
            .CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var forged = await browser.GetAsync("/api/github/oauth/callback?code=abc&state=garbage");
        Assert.Equal(HttpStatusCode.Redirect, forged.StatusCode);
        Assert.Equal("https://app.condux.test/?github=failed", forged.Headers.Location!.ToString());
    }

    /// <summary>
    /// Coming back from the Setup URL with nothing reachable means GitHub is holding the install for an
    /// organization owner to approve. Sending the user to install it again would loop forever, which is
    /// the whole reason the state carries a flag saying an install just happened.
    /// </summary>
    [Fact]
    public async Task Callback_reports_a_pending_install_rather_than_looping_back_to_install()
    {
        var app = CreateApp(NoInstallations);
        var client = app.CreateClient();
        await ApiAuth.SignUpAsync(client);
        var orgId = await CreateOrgAsync(client);

        var landed = await CallbackAsync(app, orgId, "/projects/proj-uuid", installed: true);

        Assert.Equal(
            "https://app.condux.test/projects/proj-uuid?github=pending",
            landed.Headers.Location!.ToString());
        Assert.Empty(await new GithubInstallationRepository(pg.ConnectionString).GetByOrgAsync(orgId));
    }

    /// <summary>
    /// The whole first-install path in one go, driving each leg with what the previous one actually
    /// emitted rather than with a hand-built state. The legs are tested apart elsewhere, and passing apart
    /// is not the same as joining up: the Setup URL both consumes its cookie and sets a new one on the same
    /// response, so if the replacement did not win, every install would fail at the last step with the
    /// callback answering "failed" and no other test noticing.
    /// </summary>
    [Fact]
    public async Task Connect_through_install_and_setup_links_the_installation()
    {
        var app = CreateApp(NoInstallations);
        var client = app.CreateClient();
        await ApiAuth.SignUpAsync(client);
        var orgId = await CreateOrgAsync(client);

        // 1. Connect hands back an authorize URL and remembers its state.
        var connect = await client.PostAsJsonAsync(
            $"/api/orgs/{orgId}/github/connect", new { returnPath = "/projects/proj-uuid" });
        var authorizeUrl = (await connect.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("installUrl").GetString()!;
        var connectState = Uri.UnescapeDataString(authorizeUrl.Split("state=")[1]);

        // 2. GitHub returns with no installations reachable, so the user is sent to install the app. The
        //    state to carry into the Setup URL is the one this response set, not the one we started with.
        var toInstall = await CallbackAsync(app, connectState, connectState);
        var installUrl = toInstall.Headers.Location!.ToString();
        Assert.Contains($"github.com/apps/{Slug}/installations/new", installUrl);
        var installState = Uri.UnescapeDataString(installUrl.Split("state=")[1]);
        Assert.Equal(installState, CookieFrom(toInstall));

        // 3. GitHub sends the browser to the Setup URL after the install. It writes nothing and hands the
        //    flow back to authorization, again with a state its own response remembers.
        // A unique installation id: the class shares one database, and linking refuses an installation
        // another org already holds, so reusing another test's id makes this fail only when the whole
        // class runs.
        var toAuthorize = await SetupAsync(app, 45, installState, CookieFrom(toInstall));
        var setupState = Uri.UnescapeDataString(toAuthorize.Headers.Location!.ToString().Split("state=")[1]);
        Assert.Equal(setupState, CookieFrom(toAuthorize));
        Assert.Empty(await new GithubInstallationRepository(pg.ConnectionString).GetByOrgAsync(orgId));

        // 4. This time GitHub reports the installation as reachable, so it is linked, and the browser lands
        //    back where the connect started.
        var linked = await CreateApp(Installations((45, "acme")))
            .CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false })
            .SendAsync(CallbackRequest(setupState, CookieFrom(toAuthorize)));

        Assert.Equal(
            "https://app.condux.test/projects/proj-uuid?github=connected",
            linked.Headers.Location!.ToString());
        Assert.Contains(
            await new GithubInstallationRepository(pg.ConnectionString).GetByOrgAsync(orgId),
            i => i.InstallationId == 45 && i.AccountLogin == "acme");
    }

    /// <summary>The connect-state cookie a leg set, which is the half the next leg has to send back.</summary>
    private static string CookieFrom(HttpResponseMessage response) =>
        response.Headers.GetValues("Set-Cookie")
            .Last(c => c.StartsWith($"{StateCookie}=", StringComparison.Ordinal))
            .Split(';')[0][(StateCookie.Length + 1)..];

    private static async Task<HttpResponseMessage> SetupAsync(
        WebApplicationFactory<Program> app, long installationId, string state, string cookieState)
    {
        var browser = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/github/setup?installation_id={installationId}&state={Uri.EscapeDataString(state)}");
        request.Headers.Add("Cookie", $"{StateCookie}={cookieState}");
        return await browser.SendAsync(request);
    }

    // A signed state is not authorization on its own: any org admin can mint one for their own org. Without
    // the cookie, sending that state to a victim would let the victim's own GitHub authorization finish the
    // attacker's connect, handing over the installation the victim can reach.
    [Fact]
    public async Task Callback_refuses_a_valid_state_the_browser_never_started_with()
    {
        var app = CreateApp(Installations((91, "victim-co")));
        var attacker = app.CreateClient();
        await ApiAuth.SignUpAsync(attacker);
        var attackerOrgId = await CreateOrgAsync(attacker);
        var repo = new GithubInstallationRepository(pg.ConnectionString);

        var state = GithubConnectState.Create(
            attackerOrgId, "/", false, DateTimeOffset.UtcNow.AddMinutes(10), WebhookSecret);

        var noCookie = await CallbackAsync(app, state, cookieState: null);
        Assert.Equal("https://app.condux.test/?github=failed", noCookie.Headers.Location!.ToString());
        Assert.Empty(await repo.GetByOrgAsync(attackerOrgId));

        // A cookie from some other flow is no better than none.
        var mismatched = await CallbackAsync(app, state, "a-different-state");
        Assert.Equal("https://app.condux.test/?github=failed", mismatched.Headers.Location!.ToString());
        Assert.Empty(await repo.GetByOrgAsync(attackerOrgId));
    }

    // A browser coming back from GitHub carries two halves: the state in the query, and the cookie the
    // connect leg set. Both are needed, so the helper sends both.
    private static async Task<HttpResponseMessage> CallbackAsync(
        WebApplicationFactory<Program> app, long orgId, string returnPath, bool installed = false)
    {
        var state = GithubConnectState.Create(
            orgId, returnPath, installed, DateTimeOffset.UtcNow.AddMinutes(10), WebhookSecret);
        return await CallbackAsync(app, state, state);
    }

    private static async Task<HttpResponseMessage> CallbackAsync(
        WebApplicationFactory<Program> app, string state, string? cookieState)
    {
        var browser = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        return await browser.SendAsync(CallbackRequest(state, cookieState));
    }

    private static HttpRequestMessage CallbackRequest(string state, string? cookieState)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get, $"/api/github/oauth/callback?code=the-code&state={Uri.EscapeDataString(state)}");
        if (cookieState is not null)
        {
            request.Headers.Add("Cookie", $"{StateCookie}={cookieState}");
        }
        return request;
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
            new { slug = "org-" + Guid.NewGuid().ToString("N"), name = "Org" });
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
    }
}
