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
        await Migrations.ApplyAllAsync(pg.ConnectionString);
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
        await Migrations.ApplyAllAsync(pg.ConnectionString);
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
    public async Task Start_for_an_unconfigured_domain_bounces_to_login()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var app = CreateApp(idpEmail: "x@x.test");
        var sso = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var start = await sso.GetAsync("/api/auth/sso/start?email=nobody@unknown.test");
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        Assert.Equal("/login?error=sso_not_available", start.Headers.Location!.ToString());
    }

    private static async Task<long> SetUpOrgWithSsoAsync(WebApplicationFactory<Program> app, string domain)
    {
        var admin = app.CreateClient();
        await ApiAuth.SignUpAsync(admin);
        var created = await admin.PostAsJsonAsync("/api/orgs",
            new { slug = "org-" + Guid.NewGuid().ToString("N"), name = "Org", tier = 2 }); // Business has Sso
        var orgId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();

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
