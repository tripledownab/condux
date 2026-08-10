using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Condux.IntegrationTests.Fixtures;
using Condux.Storage.Postgres;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// HTTP-level test of the control-plane auth API (#47): signup/login/logout/me over the real app
/// via WebApplicationFactory against an ephemeral Postgres. Clients keep a cookie jar
/// (HandleCookies), so a session set by signup/login round-trips to subsequent requests — exactly
/// as a same-origin browser would.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AuthApiTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    // A fresh client per call = a fresh cookie jar, i.e. a separate "browser".
    private HttpClient CreateClient() => ControlPlaneApp.Create(pg.ConnectionString).CreateClient();

    private static string UniqueEmail() => $"u-{Guid.NewGuid():N}@condux.test";

    [Fact]
    public async Task Signup_sets_a_session_and_me_returns_the_user()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var client = CreateClient();
        var email = UniqueEmail();

        var signup = await client.PostAsJsonAsync("/api/auth/signup",
            new { email, password = "correct horse battery staple" });
        Assert.Equal(HttpStatusCode.OK, signup.StatusCode);
        var body = await signup.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(email, body.GetProperty("email").GetString());
        Assert.True(body.GetProperty("id").GetInt64() > 0);

        // The session cookie carries over to /me on the same client.
        var me = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var meBody = await me.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(email, meBody.GetProperty("email").GetString());
    }

    [Fact]
    public async Task Me_without_a_cookie_is_unauthorized()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        Assert.Equal(HttpStatusCode.Unauthorized, (await CreateClient().GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Login_succeeds_with_correct_password_and_fails_with_wrong_one()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var email = UniqueEmail();
        const string password = "hunter2-hunter2";

        // Register on one client...
        await CreateClient().PostAsJsonAsync("/api/auth/signup", new { email, password });

        // ...then sign in from a clean client (no prior cookie).
        var good = await CreateClient().PostAsJsonAsync("/api/auth/login", new { email, password });
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);

        var bad = await CreateClient().PostAsJsonAsync("/api/auth/login",
            new { email, password = "wrong-password" });
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);

        var unknown = await CreateClient().PostAsJsonAsync("/api/auth/login",
            new { email = UniqueEmail(), password });
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
    }

    [Fact]
    public async Task Duplicate_signup_is_a_conflict()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var email = UniqueEmail();

        var first = await CreateClient().PostAsJsonAsync("/api/auth/signup",
            new { email, password = "first-password" });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await CreateClient().PostAsJsonAsync("/api/auth/signup",
            new { email, password = "second-password" });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Theory]
    [InlineData("not-an-email", "long-enough-password")]
    [InlineData("valid@condux.test", "short")]
    public async Task Signup_rejects_invalid_credentials(string email, string password)
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var resp = await CreateClient().PostAsJsonAsync("/api/auth/signup", new { email, password });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Providers_reports_google_disabled_when_unconfigured()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);

        // The test host sets no CONDUX_GOOGLE_*, so the login page must be told Google is off.
        var resp = await CreateClient().GetAsync("/api/auth/providers");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("google").GetBoolean());
    }

    [Fact]
    public async Task Federated_user_cannot_log_in_with_a_password()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var email = UniqueEmail();

        // A "sign in with Google" account has no local password (null hash), so the password login path
        // must reject any password rather than throw on the null hash.
        await new UserRepository(pg.ConnectionString).CreateFederatedAsync(email);

        var resp = await CreateClient().PostAsJsonAsync("/api/auth/login",
            new { email, password = "anything-at-all" });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Active_session_resolves_a_federated_user_without_a_password()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var email = UniqueEmail();
        var users = new UserRepository(pg.ConnectionString);
        var sessions = new SessionRepository(pg.ConnectionString);

        var user = await users.CreateFederatedAsync(email);
        var (_, hash) = Condux.Core.Auth.SessionTokens.Create();
        await sessions.CreateAsync(user.Id, hash, DateTimeOffset.UtcNow.AddDays(1));

        // The auth handler resolves the session on EVERY request. A federated (null password_hash) user
        // must resolve here rather than throw — regression: it used to InvalidCastException on the null
        // column, so a Google sign-in bounced the user straight back to /login.
        var resolved = await sessions.GetActiveUserAsync(hash);
        Assert.NotNull(resolved);
        Assert.Equal(email, resolved!.Email);
        Assert.Null(resolved.PasswordHash);
    }

    [Fact]
    public async Task Logout_revokes_the_session()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var client = CreateClient();
        var email = UniqueEmail();

        await client.PostAsJsonAsync("/api/auth/signup", new { email, password = "logout-me-please" });
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);

        var logout = await client.PostAsync("/api/auth/logout", content: null);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        // The (now revoked) session no longer authenticates.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }
}
