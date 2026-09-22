using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Condux.Core.Auth;
using Condux.IntegrationTests.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// "Sign in with Google" end to end (#71), against a stubbed token endpoint. Until now this route had
/// no test at all: CookieSecurityApiTest reaches /start and stops, so the callback's CSRF check, its
/// linking by verified email, the account it creates for an address nobody holds, and the MFA challenge
/// it must not skip were all unexercised.
///
/// <para>What makes it testable rather than hard is that nothing here needs a real Google. The exchange
/// is one server-to-server POST whose transport is a named HttpClient, so it is replaced; and the
/// id_token's signature is deliberately not re-verified, because it arrives on that response over TLS
/// (OIDC Core 3.1.3.7), so the claims are the whole input. Both pieces are shared with the enterprise
/// SSO tests, which drive the same exchange and the same validator.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class GoogleOAuthCallbackTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private const string ClientId = "google-client-id";
    private const string Issuer = "https://accounts.google.com";

    private WebApplicationFactory<Program> CreateApp(HttpMessageHandler tokenEndpoint) =>
        ControlPlaneApp.Create(pg.ConnectionString, configure: b =>
        {
            b.UseSetting("CONDUX_GOOGLE_CLIENT_ID", ClientId);
            b.UseSetting("CONDUX_GOOGLE_CLIENT_SECRET", "google-client-secret");
            b.UseSetting("CONDUX_GOOGLE_REDIRECT_URI",
                "https://app.example.test/api/auth/oauth/google/callback");
            // AddHttpClient<T> names the client after the type, so the transport is replaceable without
            // the test naming the internal client type.
            b.ConfigureTestServices(s => s
                .AddHttpClient("GoogleOidcClient")
                .ConfigurePrimaryHttpMessageHandler(() => tokenEndpoint));
        });

    private WebApplicationFactory<Program> AppReturning(string email, bool emailVerified = true) =>
        CreateApp(new OidcStub.TokenEndpoint(OidcStub.IdToken(Issuer, ClientId, email, emailVerified)));

    /// <summary>Walks /start so the state cookie is in the jar, then calls back with that same state.</summary>
    private static async Task<HttpResponseMessage> SignInAsync(HttpClient browser, string code = "any")
    {
        var start = await browser.GetAsync("/api/auth/oauth/google/start");
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        var state = SsoFlow.QueryParam(start.Headers.Location!, "state");
        Assert.NotEqual("", state);
        return await browser.GetAsync(
            $"/api/auth/oauth/google/callback?code={code}&state={Uri.EscapeDataString(state)}");
    }

    private static HttpClient Browser(WebApplicationFactory<Program> app) =>
        app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [Fact]
    public async Task A_verified_email_nobody_holds_gets_a_federated_account_and_a_session()
    {
        var email = $"new-{Guid.NewGuid():N}@gmail.test";
        var app = AppReturning(email);
        var browser = Browser(app);

        var callback = await SignInAsync(browser);

        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal("/", callback.Headers.Location!.ToString());
        Assert.Contains(callback.Headers.GetValues("Set-Cookie"), c => c.StartsWith("condux_session="));

        // The session the callback issued authenticates, and the account it made carries no password:
        // the provider owns that credential, and the password login path refuses a null hash.
        var me = await browser.GetFromJsonAsync<JsonElement>("/api/auth/me");
        Assert.Equal(email, me.GetProperty("email").GetString());
        var stored = await new Condux.Storage.Postgres.UserRepository(pg.ConnectionString)
            .GetByEmailAsync(email);
        Assert.Null(stored!.PasswordHash);
    }

    [Fact]
    public async Task A_second_sign_in_reuses_the_account_rather_than_making_another()
    {
        var email = $"repeat-{Guid.NewGuid():N}@gmail.test";
        var app = AppReturning(email);

        var first = Browser(app);
        Assert.Equal("/", (await SignInAsync(first)).Headers.Location!.ToString());
        var second = Browser(app);
        Assert.Equal("/", (await SignInAsync(second)).Headers.Location!.ToString());

        // Same account, not merely one row. The id is the assertion that would catch linking breaking;
        // the count alone would still pass if the second sign-in had somehow reached a different user.
        //
        // Note what does NOT do this work: the lookup the route runs before it inserts. Measured by
        // removing it, every case here still passes, because the insert settles the conflict and the
        // read after it finds the winner. That lookup saves a pointless INSERT per sign-in and is not
        // what makes the account singular; the unique index is.
        var firstId = (await first.GetFromJsonAsync<JsonElement>("/api/auth/me")).GetProperty("id").GetInt64();
        var secondId = (await second.GetFromJsonAsync<JsonElement>("/api/auth/me")).GetProperty("id").GetInt64();
        Assert.Equal(firstId, secondId);
        Assert.Equal(1, await CountUsersAsync(email));
    }

    [Fact]
    public async Task Two_callbacks_racing_one_new_address_both_sign_in_to_the_same_account()
    {
        // The claim the endpoint's own comment makes, measured rather than reasoned: both callbacks
        // find nothing, the insert settles which one wins, and LOSING is a success here, because the
        // account the winner made is the one this caller wanted. Unlike sign-up, where the same race
        // is a refusal. Each browser needs its own /start, since the state cookie is per browser.
        var email = $"race-{Guid.NewGuid():N}@gmail.test";
        var app = AppReturning(email);
        var browsers = new[] { Browser(app), Browser(app) };
        var states = await Task.WhenAll(browsers.Select(async b =>
        {
            var start = await b.GetAsync("/api/auth/oauth/google/start");
            return SsoFlow.QueryParam(start.Headers.Location!, "state");
        }));

        var callbacks = await Task.WhenAll(browsers.Zip(states, (b, state) => b.GetAsync(
            $"/api/auth/oauth/google/callback?code=any&state={Uri.EscapeDataString(state)}")));

        Assert.All(callbacks, c => Assert.Equal("/", c.Headers.Location!.ToString()));
        Assert.All(callbacks, c =>
            Assert.Contains(c.Headers.GetValues("Set-Cookie"), h => h.StartsWith("condux_session=")));
        Assert.Equal(1, await CountUsersAsync(email));
    }

    [Fact]
    public async Task An_unverified_email_is_refused_and_creates_nobody()
    {
        // The whole basis for linking accounts by address is that the provider vouched for it. Without
        // email_verified anyone who can make the provider emit a token could claim somebody's account.
        var email = $"unverified-{Guid.NewGuid():N}@gmail.test";
        var app = AppReturning(email, emailVerified: false);

        var callback = await SignInAsync(Browser(app));

        Assert.Equal("/login?error=oauth_failed", callback.Headers.Location!.ToString());
        Assert.Equal(0, await CountUsersAsync(email));
    }

    [Fact]
    public async Task A_callback_with_no_state_cookie_is_refused_and_creates_nobody()
    {
        // The state cookie is the whole CSRF defence: there is no server-side state table, so a callback
        // that did not come from a /start this browser made must go nowhere.
        var email = $"forged-{Guid.NewGuid():N}@gmail.test";
        var app = AppReturning(email);

        var forged = await Browser(app).GetAsync(
            "/api/auth/oauth/google/callback?code=any&state=a-state-nobody-issued");

        Assert.Equal("/login?error=oauth_failed", forged.Headers.Location!.ToString());
        Assert.Equal(0, await CountUsersAsync(email));
    }

    [Fact]
    public async Task A_state_that_does_not_match_the_cookie_is_refused_and_creates_nobody()
    {
        // The case above proves only that a callback with NO cookie is refused, which the
        // null check alone would do. This is the CSRF defence itself: the browser has a cookie from a
        // /start it really made, and the state in the URL is somebody else's. Measured: removing the
        // comparison leaves the case above green and only this one dies.
        var email = $"mismatch-{Guid.NewGuid():N}@gmail.test";
        var app = AppReturning(email);
        var browser = Browser(app);

        Assert.Equal(HttpStatusCode.Redirect,
            (await browser.GetAsync("/api/auth/oauth/google/start")).StatusCode);
        var callback = await browser.GetAsync(
            "/api/auth/oauth/google/callback?code=any&state=not-the-one-in-the-cookie");

        Assert.Equal("/login?error=oauth_failed", callback.Headers.Location!.ToString());
        Assert.Equal(0, await CountUsersAsync(email));
    }

    [Fact]
    public async Task A_failed_exchange_is_refused_and_creates_nobody()
    {
        // The name has to be earned. A failed exchange yields no address at all, so there is no email
        // to count: the assertion is that the table did not grow, which is the claim "creates nobody"
        // actually makes.
        var app = CreateApp(new OidcStub.FailingTokenEndpoint());
        var before = await CountAllUsersAsync();

        var callback = await SignInAsync(Browser(app));

        Assert.Equal("/login?error=oauth_failed", callback.Headers.Location!.ToString());
        Assert.Equal(before, await CountAllUsersAsync());
    }

    [Fact]
    public async Task A_password_account_with_a_second_factor_is_challenged_rather_than_signed_straight_in()
    {
        // Accounts link by verified email, so without this a user who enrolled a second factor could
        // skip it by clicking "Sign in with Google" instead of typing their password (ADR-0039).
        //
        // The account has to be a PASSWORD one. Enrolling re-asks for the password, which a federated
        // account does not have, so an account Google itself created can never reach this state: the
        // exposure is an existing local account that Google is willing to vouch for.
        const string password = "google-mfa-password-123";
        var email = $"mfa-{Guid.NewGuid():N}@gmail.test";
        var app = AppReturning(email);

        var owner = app.CreateClient();
        Assert.Equal(HttpStatusCode.OK,
            (await owner.PostAsJsonAsync("/api/auth/signup", new { email, password })).StatusCode);
        var enrol = await owner.PostAsJsonAsync("/api/auth/mfa/enroll", new { password });
        Assert.Equal(HttpStatusCode.OK, enrol.StatusCode);
        var secret = (await enrol.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("secret").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync("/api/auth/mfa/confirm",
            new { password, code = Totp.Compute(secret, Totp.StepAt(DateTimeOffset.UtcNow)) })).StatusCode);

        var viaGoogle = Browser(app);
        var callback = await SignInAsync(viaGoogle);

        Assert.Equal("/login?mfa=1", callback.Headers.Location!.ToString());
        // A cookie IS set, and that is fine: it is pending until the factor is verified. The property
        // that matters is that it authenticates nothing, which is what the flag on the session buys.
        Assert.Contains(callback.Headers.GetValues("Set-Cookie"), c => c.StartsWith("condux_session="));
        Assert.Equal(HttpStatusCode.Unauthorized, (await viaGoogle.GetAsync("/api/auth/me")).StatusCode);
    }

    /// <summary>
    /// The whole table, which only one case needs: a failed exchange yields no address to count by.
    /// Safe because xunit puts each test class in its own collection by default, so the cases here run
    /// one at a time, and PostgresFixture gives the class its own database. Both halves are load
    /// bearing. Putting this class into a shared collection, or sharing a fixture across classes,
    /// would let another test write the table between the two reads.
    /// </summary>
    private Task<int> CountAllUsersAsync() => ScalarAsync("SELECT count(*) FROM users", null);

    private Task<int> CountUsersAsync(string email) =>
        ScalarAsync("SELECT count(*) FROM users WHERE email = @email", email);

    private async Task<int> ScalarAsync(string sql, string? email)
    {
        await using var conn = new Npgsql.NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(sql, conn);
        if (email is not null)
        {
            cmd.Parameters.AddWithValue("email", email);
        }
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }
}
