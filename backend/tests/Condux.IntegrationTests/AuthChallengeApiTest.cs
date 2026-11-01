using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Condux.IntegrationTests.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// Every 401 the control-plane sends carries a WWW-Authenticate challenge (RFC 9110 15.5.2), and its
/// scheme names the credential that was refused. One case per way a 401 is produced: the session handler
/// behind RequireAuthorization, a refused password (signing in, and re-entered under a valid session), a
/// bearer route without and with a token, and a signed webhook. The MCP endpoint's challenge is pinned by
/// McpApiTest and GitHub's by GithubInstallFlowTest, alongside the 401s they already assert.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AuthChallengeApiTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private static AuthenticationHeaderValue ChallengeOf(HttpResponseMessage resp)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        return Assert.Single(resp.Headers.WwwAuthenticate);
    }

    [Fact]
    public async Task ASessionRoute_WithNoSession_ChallengesWithSession()
    {
        var client = ControlPlaneApp.Create(pg.ConnectionString).CreateClient();

        // No cookie: RequireAuthorization's challenge, from the session authentication handler.
        var me = ChallengeOf(await client.GetAsync("/api/auth/me"));
        Assert.Equal("Session", me.Scheme);
        Assert.Equal("realm=\"condux\"", me.Parameter);
    }

    [Fact]
    public async Task ARefusedPassword_ChallengesWithPassword_EvenUnderAValidSession()
    {
        var client = ControlPlaneApp.Create(pg.ConnectionString).CreateClient();

        // Signing in: the endpoint's own TypedResults.Unauthorized().
        var login = ChallengeOf(await client.PostAsJsonAsync("/api/auth/login",
            new { email = $"nobody-{Guid.NewGuid():N}@condux.test", password = "not-the-password" }));
        Assert.Equal("Password", login.Scheme);

        // Re-entered under a valid session: the cookie was accepted, so naming it would be wrong.
        await ApiAuth.SignUpAsync(client);
        var change = ChallengeOf(await client.PostAsJsonAsync("/api/auth/password",
            new { currentPassword = "not-the-password", newPassword = "a-new-password-long-enough" }));
        Assert.Equal("Password", change.Scheme);
    }

    [Fact]
    public async Task ABearerRoute_ChallengesWithBearer_AndNamesARefusedToken()
    {
        var client = ControlPlaneApp.Create(pg.ConnectionString).CreateClient();

        var none = ChallengeOf(await client.PostAsJsonAsync("/api/releases", new { version = "1.0.0" }));
        Assert.Equal("Bearer", none.Scheme);
        Assert.Equal("realm=\"condux\"", none.Parameter);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "condux_rel_bogus");
        var refused = ChallengeOf(await client.PostAsJsonAsync("/api/releases", new { version = "1.0.0" }));
        Assert.Equal("Bearer", refused.Scheme);
        Assert.Equal("realm=\"condux\", error=\"invalid_token\"", refused.Parameter);
    }

    [Fact]
    public async Task TheStripeWebhook_ChallengesWithTheHeaderItsSignatureTravelsIn()
    {
        var client = ControlPlaneApp.Create(pg.ConnectionString, configure: b =>
        {
            b.UseSetting("CONDUX_STRIPE_SECRET_KEY", "sk_test_render_only");
            b.UseSetting("CONDUX_STRIPE_WEBHOOK_SECRET", "whsec_test");
            b.UseSetting("CONDUX_STRIPE_PRICE_TEAM", "price_team_test");
            b.UseSetting("CONDUX_STRIPE_PRICE_BUSINESS", "price_business_test");
        }).CreateClient();

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/stripe/webhook") { Content = new StringContent("{}") };
        req.Headers.TryAddWithoutValidation("Stripe-Signature", "t=1,v1=deadbeef");
        var challenge = ChallengeOf(await client.SendAsync(req));
        Assert.Equal("Stripe-Signature", challenge.Scheme);
    }
}
