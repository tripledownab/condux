using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Condux.IntegrationTests.Fixtures;
using ITfoxtec.Identity.Saml2;
using ITfoxtec.Identity.Saml2.Schemas;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.IdentityModel.Tokens.Saml2;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// The SAML side of enterprise SSO (ADR-0032 slice 2): email-first /start redirects to the IdP's SSO URL
/// with a redirect-binding AuthnRequest, and the ACS accepts only a response that is signed by the org's
/// pinned certificate AND bound to this browser's in-flight request (RelayState = state cookie,
/// InResponseTo = request cookie). The tests play the IdP with the same library and a self-signed
/// certificate, so the signatures are real — a tampered or foreign-key response must fail validation, not
/// just an assertion about our own parsing.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SamlLoginFlowTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private static readonly string SecretKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private const string IdpEntityId = "https://idp.example/saml";
    private const string IdpSsoUrl = "https://idp.example/sso";
    // CONDUX_APP_BASE_URL in the test app, so the SP entity id + ACS are absolute like production.
    private const string AppBase = "https://localhost";

    private WebApplicationFactory<Program> CreateApp() =>
        ControlPlaneApp.Create(pg.ConnectionString).WithWebHostBuilder(b =>
        {
            b.UseSetting("CONDUX_SECRET_KEY", SecretKey);
            b.UseSetting("CONDUX_APP_BASE_URL", AppBase);
        });

    // The SAML state cookies are Secure, so the flow client must talk https for the jar to return them.
    private static HttpClient SsoClient(WebApplicationFactory<Program> app) =>
        app.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri(AppBase),
        });

    [Fact]
    public async Task Saml_sign_in_provisions_a_new_user_into_the_org_and_issues_a_session()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var app = CreateApp();
        var idpCertificate = SsoFlow.MakeCertificate();
        var orgId = await SetUpOrgWithSamlAsync(app, "acme.test", idpCertificate);

        var sso = SsoClient(app);
        var start = await sso.GetAsync("/api/auth/sso/start?email=alice@acme.test");
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        var location = start.Headers.Location!.ToString();
        Assert.StartsWith(IdpSsoUrl, location);
        Assert.Contains("SAMLRequest=", location);
        var relayState = SsoFlow.QueryParam(new Uri(location), "RelayState");
        var requestId = CookieValue(start, "condux_saml_req");
        Assert.NotEqual("", requestId);

        var samlResponse = SignedResponse(idpCertificate, requestId, "alice@acme.test");
        var acs = await PostAcsAsync(sso, samlResponse, relayState);
        Assert.Equal(HttpStatusCode.Redirect, acs.StatusCode);
        Assert.Equal("/", acs.Headers.Location!.ToString());
        Assert.Contains(acs.Headers.GetValues("Set-Cookie"), c => c.StartsWith("condux_session="));
        Assert.Equal(1, await SsoFlow.CountMembershipAsync(pg.ConnectionString, orgId, "alice@acme.test"));
    }

    [Fact]
    public async Task A_response_signed_by_a_different_certificate_is_refused()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var app = CreateApp();
        var orgId = await SetUpOrgWithSamlAsync(app, "beta.test", SsoFlow.MakeCertificate());

        var sso = SsoClient(app);
        var start = await sso.GetAsync("/api/auth/sso/start?email=alice@beta.test");
        var relayState = SsoFlow.QueryParam(start.Headers.Location!, "RelayState");
        var requestId = CookieValue(start, "condux_saml_req");

        // Signed by a key the org never pinned — a forged IdP.
        var forged = SignedResponse(SsoFlow.MakeCertificate(), requestId, "alice@beta.test");
        var acs = await PostAcsAsync(sso, forged, relayState);
        Assert.Equal("/login?error=sso_failed", acs.Headers.Location!.ToString());
        Assert.Equal(0, await SsoFlow.CountMembershipAsync(pg.ConnectionString, orgId, "alice@beta.test"));
    }

    [Fact]
    public async Task A_tampered_response_is_refused()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var app = CreateApp();
        var idpCertificate = SsoFlow.MakeCertificate();
        var orgId = await SetUpOrgWithSamlAsync(app, "gamma.test", idpCertificate);

        var sso = SsoClient(app);
        var start = await sso.GetAsync("/api/auth/sso/start?email=alice@gamma.test");
        var relayState = SsoFlow.QueryParam(start.Headers.Location!, "RelayState");
        var requestId = CookieValue(start, "condux_saml_req");

        // A validly-signed response whose asserted email is then edited: the signature must catch it.
        var signed = SignedResponse(idpCertificate, requestId, "alice@gamma.test");
        var tampered = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(Convert.FromBase64String(signed))
                .Replace("alice@gamma.test", "mallory@gamma.test")));
        var acs = await PostAcsAsync(sso, tampered, relayState);
        Assert.Equal("/login?error=sso_failed", acs.Headers.Location!.ToString());
        Assert.Equal(0, await SsoFlow.CountMembershipAsync(pg.ConnectionString, orgId, "mallory@gamma.test"));
    }

    [Fact]
    public async Task A_response_with_its_signature_stripped_is_refused()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var app = CreateApp();
        var idpCertificate = SsoFlow.MakeCertificate();
        var orgId = await SetUpOrgWithSamlAsync(app, "zeta.test", idpCertificate);

        var sso = SsoClient(app);
        var start = await sso.GetAsync("/api/auth/sso/start?email=alice@zeta.test");
        var relayState = SsoFlow.QueryParam(start.Headers.Location!, "RelayState");
        var requestId = CookieValue(start, "condux_saml_req");

        // Everything legitimate except no signature at all — "unsigned" must not read as "nothing failed".
        var signed = SignedResponse(idpCertificate, requestId, "alice@zeta.test");
        var stripped = Convert.ToBase64String(Encoding.UTF8.GetBytes(Regex.Replace(
            Encoding.UTF8.GetString(Convert.FromBase64String(signed)),
            "<(\\w+:)?Signature .*?</(\\w+:)?Signature>", "", RegexOptions.Singleline)));
        Assert.DoesNotContain("SignatureValue", Encoding.UTF8.GetString(Convert.FromBase64String(stripped)));
        var acs = await PostAcsAsync(sso, stripped, relayState);
        Assert.Equal("/login?error=sso_failed", acs.Headers.Location!.ToString());
        Assert.Equal(0, await SsoFlow.CountMembershipAsync(pg.ConnectionString, orgId, "alice@zeta.test"));
    }

    [Fact]
    public async Task An_unsolicited_response_with_no_in_flight_request_is_refused()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var app = CreateApp();
        var idpCertificate = SsoFlow.MakeCertificate();
        var orgId = await SetUpOrgWithSamlAsync(app, "delta.test", idpCertificate);

        // A perfectly-signed response, but this browser never started a login (no cookies) — the
        // IdP-initiated / replay case.
        var unsolicited = SignedResponse(idpCertificate, "_" + Guid.NewGuid().ToString("N"), "alice@delta.test");
        var acs = await PostAcsAsync(SsoClient(app), unsolicited, relayState: "anything");
        Assert.Equal("/login?error=sso_failed", acs.Headers.Location!.ToString());
        Assert.Equal(0, await SsoFlow.CountMembershipAsync(pg.ConnectionString, orgId, "alice@delta.test"));
    }

    [Fact]
    public async Task A_response_answering_a_different_request_is_refused()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var app = CreateApp();
        var idpCertificate = SsoFlow.MakeCertificate();
        var orgId = await SetUpOrgWithSamlAsync(app, "epsilon.test", idpCertificate);

        var sso = SsoClient(app);
        var start = await sso.GetAsync("/api/auth/sso/start?email=alice@epsilon.test");
        var relayState = SsoFlow.QueryParam(start.Headers.Location!, "RelayState");

        // Right key, right RelayState, but InResponseTo names a request this browser never made.
        var crossed = SignedResponse(idpCertificate, "_" + Guid.NewGuid().ToString("N"), "alice@epsilon.test");
        var acs = await PostAcsAsync(sso, crossed, relayState);
        Assert.Equal("/login?error=sso_failed", acs.Headers.Location!.ToString());
        Assert.Equal(0, await SsoFlow.CountMembershipAsync(pg.ConnectionString, orgId, "alice@epsilon.test"));
    }

    private static async Task<HttpResponseMessage> PostAcsAsync(
        HttpClient sso, string samlResponse, string relayState) =>
        await sso.PostAsync("/api/auth/sso/saml/acs", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["SAMLResponse"] = samlResponse,
            ["RelayState"] = relayState,
        }));

    /// <summary>Play the IdP: a signed SAML response asserting <paramref name="email"/>, answering
    /// <paramref name="requestId"/>. Real XML signature via the same library, keyed by the given cert.</summary>
    private static string SignedResponse(X509Certificate2 idpCertificate, string requestId, string email)
    {
        var idpConfig = new Saml2Configuration
        {
            Issuer = IdpEntityId,
            SigningCertificate = idpCertificate,
        };
        var response = new Saml2AuthnResponse(idpConfig)
        {
            InResponseTo = new Saml2Id(requestId),
            Status = Saml2StatusCodes.Success,
            Destination = new Uri($"{AppBase}/api/auth/sso/saml/acs"),
            SessionIndex = Guid.NewGuid().ToString(),
            NameId = new Saml2NameIdentifier(email, NameIdentifierFormats.Email),
            ClaimsIdentity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, email),
                new Claim(ClaimTypes.Email, email),
            ]),
        };
        response.CreateSecurityToken(
            $"{AppBase}/api/auth/sso/saml", subjectConfirmationLifetime: 5, issuedTokenLifetime: 60);

        var binding = new Saml2PostBinding();
        binding.Bind(response);
        var match = Regex.Match(binding.PostContent, "name=\"SAMLResponse\" value=\"([^\"]+)\"");
        Assert.True(match.Success, "the post binding should carry the encoded response");
        return match.Groups[1].Value;
    }


    // Not static: reaches pg.ConnectionString to set the tier out of band, since POST /api/orgs
    // always creates Free and only the Stripe webhook moves an org off it.
    private async Task<long> SetUpOrgWithSamlAsync(
        WebApplicationFactory<Program> app, string domain, X509Certificate2 idpCertificate)
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
            issuer = IdpEntityId,
            protocol = 1, // SAML
            samlSsoUrl = IdpSsoUrl,
            samlCertificate = idpCertificate.ExportCertificatePem(),
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        return orgId;
    }

    private static string CookieValue(HttpResponseMessage response, string name)
    {
        foreach (var header in response.Headers.GetValues("Set-Cookie"))
        {
            if (header.StartsWith(name + "="))
            {
                return Uri.UnescapeDataString(header[(name.Length + 1)..header.IndexOf(';')]);
            }
        }
        return "";
    }


}
