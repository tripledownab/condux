using System.Net;
using System.Net.Http.Json;
using Condux.IntegrationTests.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// Every cookie the control-plane writes carries <c>Secure</c> when the deployment is served over https,
/// and none does when it is not.
///
/// <para>These run over the plain-http TestServer on purpose, because that is the situation production is
/// in: TLS ends at the edge proxy and the inbound request is http, so the request cannot answer the
/// question and only the deployment's declared base URL can. Asserting over an https request instead
/// would pass against the old per-writer <c>Request.IsHttps</c> check, which is the defect.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class CookieSecurityApiTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    /// <summary>
    /// Sets the base URL through <c>configure</c>, NOT through <c>ControlPlaneApp.Create</c>'s
    /// <c>appBaseUrl</c>. That parameter deliberately moves the client to https to match, which every
    /// other test wants and this one must not have: the mismatch, an https deployment reached over
    /// plain http, is the production shape being asserted.
    /// </summary>
    private WebApplicationFactory<Program> App(string? baseUrl, bool google = false) =>
        ControlPlaneApp.Create(pg.ConnectionString, configure: b =>
        {
            if (baseUrl is not null)
            {
                b.UseSetting("CONDUX_APP_BASE_URL", baseUrl);
            }
            if (google)
            {
                b.UseSetting("CONDUX_GOOGLE_CLIENT_ID", "client-id");
                b.UseSetting("CONDUX_GOOGLE_CLIENT_SECRET", "client-secret");
                b.UseSetting("CONDUX_GOOGLE_REDIRECT_URI", "https://app.example.test/api/auth/oauth/google/callback");
            }
        });

    /// <summary>The Set-Cookie line for one cookie name, or null when the response set no such cookie.</summary>
    private static string? SetCookie(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.FirstOrDefault(v => v.StartsWith(name + "=", StringComparison.Ordinal))
            : null;

    private static bool IsSecure(string setCookie) =>
        setCookie.Split(';').Any(a => a.Trim().Equals("secure", StringComparison.OrdinalIgnoreCase));

    [Fact]
    public async Task Session_cookie_is_secure_on_an_https_deployment_and_plain_on_an_http_one()
    {
        var secured = await App("https://app.example.test").CreateClient().PostAsJsonAsync(
            "/api/auth/signup", new { email = $"u-{Guid.NewGuid():N}@condux.test", password = "test-password-123" });
        secured.EnsureSuccessStatusCode();
        var securedCookie = SetCookie(secured, "condux_session");
        Assert.NotNull(securedCookie);
        Assert.True(IsSecure(securedCookie), $"expected Secure on an https deployment, got: {securedCookie}");

        // The other direction, which is what keeps plain-http local dev and this TestServer working. It
        // also stops the assertion above being satisfiable by hardcoding Secure on.
        var plain = await App(baseUrl: null).CreateClient().PostAsJsonAsync(
            "/api/auth/signup", new { email = $"u-{Guid.NewGuid():N}@condux.test", password = "test-password-123" });
        plain.EnsureSuccessStatusCode();
        var plainCookie = SetCookie(plain, "condux_session");
        Assert.NotNull(plainCookie);
        Assert.False(IsSecure(plainCookie), $"expected no Secure on an http deployment, got: {plainCookie}");
    }

    /// <summary>
    /// The cookie whose writer never mentions <c>Secure</c> at all. It is the one that proves the rule has
    /// a single owner rather than three agreeing copies: the CSRF-state options say nothing about
    /// transport, so the attribute can only have come from the pipeline, and a cookie added tomorrow
    /// inherits it the same way.
    /// </summary>
    [Fact]
    public async Task A_csrf_state_cookie_inherits_the_deployment_policy_without_asking_for_it()
    {
        var client = App("https://app.example.test", google: true)
            .CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/api/auth/oauth/google/start");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var state = SetCookie(response, "condux_oauth_state");
        Assert.NotNull(state);
        Assert.True(IsSecure(state), $"expected Secure on an https deployment, got: {state}");
    }
}
