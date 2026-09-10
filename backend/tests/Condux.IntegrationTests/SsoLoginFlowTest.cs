using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Condux.IntegrationTests.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// The enterprise-SSO sign-in flow (#72): email-first /start routes to the org's IdP, /callback exchanges
/// the code (against a stubbed IdP), asserts the returned email belongs to the org's domain, and JIT
/// provisions the user into the org before issuing the session. The single-org invariant itself lives in
/// the shared MembershipProvisioning service (covered via invite-accept); here we prove the SSO wiring.
///
/// Also pins who an org's provider may sign in (ADR-0042): an address nobody holds, or one held by a
/// member the org already has, and nobody else. The org supplies both the provider and the domain, so
/// this rule is what makes an assertion from it worth honouring. Both directions are covered, because a
/// rule that refused everything would satisfy a test that only checks the refusal.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SsoLoginFlowTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private static readonly string SecretKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    // A stub IdP token endpoint: every exchange returns an id_token for the same configured email. The
    // signature is not checked (server-to-server over TLS), so a static header.payload.sig token is enough.
    private sealed class StubIdp(string idToken) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{"id_token":"{{idToken}}"}""", Encoding.UTF8, "application/json"),
            });
    }

    private WebApplicationFactory<Program> CreateApp(string idpEmail) =>
        ControlPlaneApp.Create(pg.ConnectionString).WithWebHostBuilder(b =>
        {
            b.UseSetting("CONDUX_SECRET_KEY", SecretKey);
            // Stub the SSO exchange client's transport by its named-client name (AddHttpClient<T> names the
            // client after the type), so the token endpoint is never actually called. Keeps the internal
            // SsoOidcClient type out of the test.
            b.ConfigureTestServices(s => s
                .AddHttpClient("SsoOidcClient")
                .ConfigurePrimaryHttpMessageHandler(() => new StubIdp(IdToken(idpEmail))));
        });

    [Fact]
    public async Task Sign_in_provisions_a_new_user_into_the_org_and_issues_a_session()
    {
        var app = CreateApp(idpEmail: "alice@acme.test");
        var orgId = await SetUpOrgWithSsoAsync(app, "acme.test");

        var sso = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var start = await sso.GetAsync("/api/auth/sso/start?email=alice@acme.test");
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        var authorize = start.Headers.Location!.ToString();
        Assert.StartsWith("https://idp.example/authorize", authorize);
        var state = SsoFlow.QueryParam(new Uri(authorize), "state");
        Assert.NotEqual("", state);

        // The state cookie + org cookie ride the client's jar back to the callback.
        var callback = await sso.GetAsync($"/api/auth/sso/callback?code=any&state={Uri.EscapeDataString(state)}");
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal("/", callback.Headers.Location!.ToString());
        Assert.Contains(callback.Headers.GetValues("Set-Cookie"), c => c.StartsWith("condux_session="));
        Assert.Equal(1, await SsoFlow.CountMembershipAsync(pg.ConnectionString, orgId, "alice@acme.test"));
    }

    [Fact]
    public async Task A_returned_email_outside_the_org_domain_is_refused_and_provisions_nobody()
    {
        var app = CreateApp(idpEmail: "mallory@evil.test"); // IdP returns an email off the org's domain
        // A distinct domain — email_domain is globally unique and this class shares one fixture DB.
        var orgId = await SetUpOrgWithSsoAsync(app, "beta.test");

        var sso = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var start = await sso.GetAsync("/api/auth/sso/start?email=alice@beta.test");
        var state = SsoFlow.QueryParam(new Uri(start.Headers.Location!.ToString()), "state");

        var callback = await sso.GetAsync($"/api/auth/sso/callback?code=any&state={Uri.EscapeDataString(state)}");
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal("/login?error=sso_domain_mismatch", callback.Headers.Location!.ToString());
        Assert.Equal(0, await SsoFlow.CountMembershipAsync(pg.ConnectionString, orgId, "mallory@evil.test"));
    }

    [Fact]
    public async Task An_existing_account_the_org_does_not_have_is_refused_and_keeps_its_own_org()
    {
        const string victimEmail = "victim@claimed.test";
        var app = CreateApp(idpEmail: victimEmail);

        // Someone already holds that address and has an org of their own. Signup alone does not give
        // them one (ADR-0018 moved that into onboarding), so the test creates it the way onboarding does.
        var victimClient = app.CreateClient();
        await ApiAuth.SignUpAsync(victimClient, victimEmail);
        var own = await victimClient.PostAsJsonAsync("/api/orgs",
            new { slug = "org-" + Guid.NewGuid().ToString("N"), name = "Their own org" });
        Assert.Equal(HttpStatusCode.Created, own.StatusCode);
        var ownOrgId = await OnlyOrgIdAsync(victimClient);

        // A different org then claims the domain and points its IdP at that same address.
        var orgId = await SetUpOrgWithSsoAsync(app, "claimed.test");
        Assert.NotEqual(ownOrgId, orgId);

        var sso = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var start = await sso.GetAsync($"/api/auth/sso/start?email={victimEmail}");
        var state = SsoFlow.QueryParam(new Uri(start.Headers.Location!.ToString()), "state");

        var callback = await sso.GetAsync($"/api/auth/sso/callback?code=any&state={Uri.EscapeDataString(state)}");

        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal("/login?error=sso_invite_required", callback.Headers.Location!.ToString());
        IEnumerable<string> setCookies =
            callback.Headers.TryGetValues("Set-Cookie", out var values) ? values : [];
        Assert.DoesNotContain(setCookies, c => c.StartsWith("condux_session="));
        Assert.Equal(0, await SsoFlow.CountMembershipAsync(pg.ConnectionString, orgId, victimEmail));

        // Their own org is still there and still theirs, so nothing was moved or deleted on the way.
        Assert.Equal(ownOrgId, await OnlyOrgIdAsync(victimClient));
    }

    [Fact]
    public async Task An_existing_account_with_no_org_at_all_is_refused_rather_than_absorbed()
    {
        // Signup creates the account but no org (ADR-0018 moved that into onboarding), so every user
        // who stops before finishing sits with no membership at all. That has to read as "not a member
        // of this org", not as "unclaimed": it is the emptiest possible state, not the freest.
        const string strandedEmail = "stranded@orphan.test";
        var app = CreateApp(idpEmail: strandedEmail);
        await ApiAuth.SignUpAsync(app.CreateClient(), strandedEmail);
        var orgId = await SetUpOrgWithSsoAsync(app, "orphan.test");

        var sso = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var start = await sso.GetAsync($"/api/auth/sso/start?email={strandedEmail}");
        var state = SsoFlow.QueryParam(new Uri(start.Headers.Location!.ToString()), "state");

        var callback = await sso.GetAsync($"/api/auth/sso/callback?code=any&state={Uri.EscapeDataString(state)}");

        Assert.Equal("/login?error=sso_invite_required", callback.Headers.Location!.ToString());
        Assert.Equal(0, await SsoFlow.CountMembershipAsync(pg.ConnectionString, orgId, strandedEmail));
    }

    [Fact]
    public async Task A_member_provisioned_by_an_earlier_sign_in_can_sign_in_again()
    {
        const string email = "bob@repeat.test";
        var app = CreateApp(idpEmail: email);
        var orgId = await SetUpOrgWithSsoAsync(app, "repeat.test");

        async Task<HttpResponseMessage> SignInAsync()
        {
            var sso = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var start = await sso.GetAsync($"/api/auth/sso/start?email={email}");
            var state = SsoFlow.QueryParam(new Uri(start.Headers.Location!.ToString()), "state");
            return await sso.GetAsync($"/api/auth/sso/callback?code=any&state={Uri.EscapeDataString(state)}");
        }

        var first = await SignInAsync();
        Assert.Equal("/", first.Headers.Location!.ToString());

        // The address now exists, which is exactly the case the rule refuses for a stranger. A member
        // must still get through, or every SSO user would be locked out after their first sign-in.
        var second = await SignInAsync();
        Assert.Equal("/", second.Headers.Location!.ToString());
        Assert.Contains(second.Headers.GetValues("Set-Cookie"), c => c.StartsWith("condux_session="));
        Assert.Equal(1, await SsoFlow.CountMembershipAsync(pg.ConnectionString, orgId, email));
    }

    [Fact]
    public async Task The_advertised_redirect_uri_is_the_one_the_authorize_request_actually_carries()
    {
        // The settings tab shows this string and the admin registers it in their IdP, while the server
        // sends its own in the authorize request and again in the token exchange. If the two ever differ
        // the admin registered exactly what we told them to and their IdP still rejects it, so the only
        // useful assertion is that both sides produce the same value.
        var app = CreateApp(idpEmail: "alice@meta.test");
        await SetUpOrgWithSsoAsync(app, "meta.test");

        var admin = app.CreateClient();
        await ApiAuth.SignUpAsync(admin);
        var metadata = await admin.GetFromJsonAsync<JsonElement>("/api/auth/sso/metadata");
        var advertised = metadata.GetProperty("redirectUri").GetString();

        var sso = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var start = await sso.GetAsync("/api/auth/sso/start?email=alice@meta.test");
        var sent = SsoFlow.QueryParam(new Uri(start.Headers.Location!.ToString()), "redirect_uri");

        Assert.Equal(advertised, sent);
        Assert.EndsWith("/api/auth/sso/callback", sent);
    }

    [Fact]
    public async Task Start_for_an_unconfigured_domain_bounces_to_login()
    {
        var app = CreateApp(idpEmail: "x@x.test");
        var sso = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var start = await sso.GetAsync("/api/auth/sso/start?email=nobody@unknown.test");
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        Assert.Equal("/login?error=sso_not_available", start.Headers.Location!.ToString());
    }

    // The org this client's user belongs to, asserting there is exactly one (ADR-0018). Reading it back
    // through the API is what proves the org still exists AND still holds them: a direct row count would
    // pass just as happily if they had been moved into somebody else's org.
    private static async Task<long> OnlyOrgIdAsync(HttpClient client)
    {
        var orgs = await client.GetFromJsonAsync<JsonElement>("/api/orgs");
        Assert.Equal(1, orgs.GetArrayLength());
        return orgs[0].GetProperty("org").GetProperty("id").GetInt64();
    }

    // Not static: reaches pg.ConnectionString to set the tier out of band, since POST /api/orgs
    // always creates Free and only the Stripe webhook moves an org off it.
    private async Task<long> SetUpOrgWithSsoAsync(WebApplicationFactory<Program> app, string domain)
    {
        var admin = app.CreateClient();
        await ApiAuth.SignUpAsync(admin);
        var created = await admin.PostAsJsonAsync("/api/orgs",
            new { slug = "org-" + Guid.NewGuid().ToString("N"), name = "Org" }); // Business has Sso
        var orgId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        await OrgSeed.SetTierAsync(pg.ConnectionString, orgId, 2);

        var put = await admin.PutAsJsonAsync($"/api/orgs/{orgId}/sso-config", new
        {
            emailDomain = domain,
            issuer = "https://idp.example/",
            authorizationEndpoint = "https://idp.example/authorize",
            tokenEndpoint = "https://idp.example/token",
            clientId = "client-abc",
            clientSecret = "super-secret-value-xyz",
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        return orgId;
    }

    // The stub IdP's id_token: aud + iss match the config, verified email, non-expired.
    private static string IdToken(string email) => Token(new
    {
        aud = "client-abc",
        iss = "https://idp.example/",
        exp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600,
        email_verified = true,
        email,
        sub = "idp-" + email,
    });

    private static string Token(object payload)
    {
        var header = Base64Url(Encoding.UTF8.GetBytes("""{"alg":"RS256","typ":"JWT"}"""));
        var body = Base64Url(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)));
        return $"{header}.{body}.signature-not-checked";
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');


}
