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

    /// <summary>
    /// Changing a password has to do three things, and the third is the one that is easy to omit: prove
    /// the caller knows the current password, replace it, and stop every OTHER session. A session opened
    /// with the old password outliving the change is the whole reason someone rotates one.
    /// </summary>
    [Fact]
    public async Task Change_password_reauthenticates_swaps_the_credential_and_ends_other_sessions()
    {
        var email = UniqueEmail();
        var app = ControlPlaneApp.Create(pg.ConnectionString);

        // Two browsers signed in as the same user: the one changing the password, and another that must
        // not survive it.
        var changing = app.CreateClient();
        await changing.PostAsJsonAsync("/api/auth/signup", new { email, password = "old-password-123" });
        var other = app.CreateClient();
        await other.PostAsJsonAsync("/api/auth/login", new { email, password = "old-password-123" });
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/api/auth/me")).StatusCode);

        // The wrong current password changes nothing, and says only "no".
        var wrong = await changing.PostAsJsonAsync("/api/auth/password",
            new { currentPassword = "not-the-password", newPassword = "new-password-456" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        // Too short is refused before the hasher sees it.
        var tooShort = await changing.PostAsJsonAsync("/api/auth/password",
            new { currentPassword = "old-password-123", newPassword = "short" });
        Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);

        var changed = await changing.PostAsJsonAsync("/api/auth/password",
            new { currentPassword = "old-password-123", newPassword = "new-password-456" });
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);

        // The caller keeps their own session; the other browser is signed out.
        Assert.Equal(HttpStatusCode.OK, (await changing.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.GetAsync("/api/auth/me")).StatusCode);

        // The credential really swapped, in both directions.
        var fresh = app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await fresh.PostAsJsonAsync("/api/auth/login", new { email, password = "old-password-123" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await fresh.PostAsJsonAsync("/api/auth/login", new { email, password = "new-password-456" })).StatusCode);
    }

    [Fact]
    public async Task Change_password_requires_a_session()
    {
        var anonymous = CreateClient();

        var resp = await anonymous.PostAsJsonAsync("/api/auth/password",
            new { currentPassword = "whatever-123", newPassword = "new-password-456" });

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Signup_sets_a_session_and_me_returns_the_user()
    {
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
        Assert.Equal(HttpStatusCode.Unauthorized, (await CreateClient().GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Login_succeeds_with_correct_password_and_fails_with_wrong_one()
    {
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
        var resp = await CreateClient().PostAsJsonAsync("/api/auth/signup", new { email, password });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Providers_reports_google_disabled_when_unconfigured()
    {
        // The test host sets no CONDUX_GOOGLE_*, so the login page must be told Google is off.
        var resp = await CreateClient().GetAsync("/api/auth/providers");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("google").GetBoolean());
    }

    [Fact]
    public async Task Federated_user_cannot_log_in_with_a_password()
    {
        var email = UniqueEmail();

        // A "sign in with Google" account has no local password (null hash), so the password login path
        // must reject any password rather than throw on the null hash.
        await new UserRepository(pg.ConnectionString).TryCreateFederatedAsync(email);

        var resp = await CreateClient().PostAsJsonAsync("/api/auth/login",
            new { email, password = "anything-at-all" });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Active_session_resolves_a_federated_user_without_a_password()
    {
        var email = UniqueEmail();
        var users = new UserRepository(pg.ConnectionString);
        var sessions = new SessionRepository(pg.ConnectionString);

        var user = await users.TryCreateFederatedAsync(email)
            ?? throw new InvalidOperationException("seed address was taken");
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
        var client = CreateClient();
        var email = UniqueEmail();

        await client.PostAsJsonAsync("/api/auth/signup", new { email, password = "logout-me-please" });
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);

        var logout = await client.PostAsync("/api/auth/logout", content: null);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        // The (now revoked) session no longer authenticates.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task A_platform_admin_address_cannot_be_signed_up_for()
    {
        // The claim that opens the cross-tenant console is stamped from the env allowlist on every
        // request, and signup proves nothing about the address it hands out, so an allowlisted
        // address that nobody had registered yet was first come, first served. Found live on a
        // deployment where the address was listed and never claimed.
        const string reserved = "reserved-admin@condux.test";
        // seedPlatformAdmin: false is the whole point. With the row present the earlier
        // email_taken branch answers first, and the refusal below is never reached.
        var app = ControlPlaneApp.Create(
            pg.ConnectionString, platformAdminEmails: reserved, seedPlatformAdmin: false);
        var attacker = app.CreateClient();

        var taken = await attacker.PostAsJsonAsync("/api/auth/signup",
            new { email = reserved, password = "attacker-password-123" });
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        Assert.Equal("platform_admin_reserved",
            (await taken.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());

        // No session was issued, so nothing downstream can be reached as that address.
        Assert.Equal(HttpStatusCode.Unauthorized, (await attacker.GetAsync("/api/auth/me")).StatusCode);

        // Casing must not be a way round it: the allowlist normalises, and so must this.
        var cased = await attacker.PostAsJsonAsync("/api/auth/signup",
            new { email = "Reserved-Admin@Condux.Test", password = "attacker-password-123" });
        Assert.Equal(HttpStatusCode.Conflict, cased.StatusCode);

        // An address that is not allowlisted still signs up normally.
        var ordinary = app.CreateClient();
        var ok = await ordinary.PostAsJsonAsync("/api/auth/signup",
            new { email = UniqueEmail(), password = "ordinary-password-123" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task An_allowlisted_address_that_is_registered_answers_like_any_other_taken_one()
    {
        // The reserved check sits AFTER the taken check, so the explanatory code only appears in the
        // state it explains. Published as a guarantee: a listed address that somebody holds reveals
        // nothing it did not reveal before, which stops the refusal becoming a way to ask whether a
        // given address is a platform admin.
        const string reserved = "held-admin@condux.test";
        var app = ControlPlaneApp.Create(pg.ConnectionString, platformAdminEmails: reserved);

        var resp = await app.CreateClient().PostAsJsonAsync(
            "/api/auth/signup", new { email = reserved, password = "attacker-password-123" });

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Equal("email_taken",
            (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Racing_sign_ups_for_one_address_give_a_conflict_and_never_a_500()
    {
        // Every caller looks the address up and then inserts it, and the gap between the two is
        // reachable: a double-clicked submit button is enough. Before the insert handled the conflict
        // this returned 500 on every run, carrying a Postgres exception that the platform then filed
        // against itself as a defect.
        var app = ControlPlaneApp.Create(pg.ConnectionString);
        var email = UniqueEmail();

        var responses = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ =>
            app.CreateClient().PostAsJsonAsync(
                "/api/auth/signup", new { email, password = "racing-signups-123" })));

        var codes = responses.Select(r => r.StatusCode).OrderBy(c => c).ToArray();
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], codes);
        Assert.Equal("email_taken", (await responses
            .First(r => r.StatusCode == HttpStatusCode.Conflict)
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }
}
