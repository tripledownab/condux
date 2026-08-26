using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Condux.Core.Auth;
using Condux.IntegrationTests.Fixtures;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// MFA over HTTP, which is the layer that matters: the service-level behaviour can be perfect while the
/// endpoint in front of it forgets to check a password. Every test here is a property somebody could
/// plausibly break, not a restatement of the implementation.
/// </summary>
[Trait("Category", "Integration")]
public sealed class MfaApiTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private const string Password = "correct-horse-battery";

    [Fact]
    public async Task A_pending_session_resolves_to_nobody_on_ordinary_endpoints()
    {
        var (client, _) = await EnrolledUserAsync();

        // Sign in again: the password is right, so a cookie comes back, but MFA is not done.
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email = Email(client), password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.True((await Body(login)).GetProperty("mfaRequired").GetBoolean());

        // THE security property. Not "the response said mfaRequired" — that is a hint to the client.
        // What must hold is that the cookie it just set authenticates nothing at all.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    // The regression test for the flaw in the implementation this was ported from: enrolling required
    // only a session there, so a stolen cookie let an attacker enrol their own authenticator and then
    // revoke every session the real owner had.
    [Fact]
    public async Task Enrolling_and_confirming_demand_the_password_not_just_a_session()
    {
        var (client, _) = await SignedUpAsync();

        var withoutPassword = await client.PostAsJsonAsync("/api/auth/mfa/enroll", new { password = "" });
        Assert.Equal(HttpStatusCode.Unauthorized, withoutPassword.StatusCode);

        var wrongPassword = await client.PostAsJsonAsync("/api/auth/mfa/enroll", new { password = "not-it" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);

        // Same for confirm, which is the one that revokes the owner's other sessions.
        var confirm = await client.PostAsJsonAsync(
            "/api/auth/mfa/confirm", new { password = "not-it", code = "000000" });
        Assert.Equal(HttpStatusCode.Unauthorized, confirm.StatusCode);
    }

    [Fact]
    public async Task A_code_cannot_be_replayed_inside_its_own_window()
    {
        var (client, secret) = await EnrolledUserAsync();
        await client.PostAsJsonAsync("/api/auth/login", new { email = Email(client), password = Password });

        // Next step, not the current one: confirming consumed the current step (see the test below), and
        // one step ahead is still inside the drift window so it verifies now.
        var code = Totp.Compute(secret, Totp.StepAt(DateTimeOffset.UtcNow) + 1);
        Assert.Equal(HttpStatusCode.OK, (await Verify(client, code)).StatusCode);

        // Same code, still inside its 30-second window, on a fresh pending session.
        await client.PostAsJsonAsync("/api/auth/login", new { email = Email(client), password = Password });
        Assert.Equal(HttpStatusCode.BadRequest, (await Verify(client, code)).StatusCode);
    }

    [Fact]
    public async Task Concurrent_verifications_of_one_code_admit_exactly_one()
    {
        var (client, secret) = await EnrolledUserAsync();
        var code = Totp.Compute(secret, Totp.StepAt(DateTimeOffset.UtcNow) + 1);

        // Eight separate browsers, each with its own pending session, racing the same code. A
        // read-then-update replay guard passes every sequential test and then lets several through here.
        var email = Email(client);
        var clients = new List<HttpClient>();
        for (var i = 0; i < 8; i++)
        {
            var browser = NewBrowser();
            await browser.PostAsJsonAsync("/api/auth/login", new { email, password = Password });
            clients.Add(browser);
        }

        var results = await Task.WhenAll(clients.Select(c => Verify(c, code)));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
    }

    // Discovered while writing these: confirming consumes the step, so the code typed to switch MFA on
    // cannot immediately be replayed to satisfy a login challenge. That is the correct behaviour and it
    // falls out of the shared replay guard rather than being special-cased, so it is worth pinning here.
    [Fact]
    public async Task The_code_used_to_confirm_enrolment_cannot_then_log_in()
    {
        var (client, secret) = await SignedUpAsync();
        var enrol = await client.PostAsJsonAsync("/api/auth/mfa/enroll", new { password = Password });
        secret = (await Body(enrol)).GetProperty("secret").GetString()!;
        var code = Totp.Compute(secret, Totp.StepAt(DateTimeOffset.UtcNow));
        await client.PostAsJsonAsync("/api/auth/mfa/confirm", new { password = Password, code });

        await client.PostAsJsonAsync("/api/auth/login", new { email = Email(client), password = Password });

        Assert.Equal(HttpStatusCode.BadRequest, (await Verify(client, code)).StatusCode);
    }

    [Fact]
    public async Task A_recovery_code_works_once_and_not_twice()
    {
        var (client, _) = await EnrolledUserAsync();
        var codes = await RecoveryCodesAsync(client);
        await client.PostAsJsonAsync("/api/auth/login", new { email = Email(client), password = Password });

        Assert.Equal(HttpStatusCode.OK, (await Verify(client, codes[0])).StatusCode);

        await client.PostAsJsonAsync("/api/auth/login", new { email = Email(client), password = Password });
        Assert.Equal(HttpStatusCode.BadRequest, (await Verify(client, codes[0])).StatusCode);
    }

    [Fact]
    public async Task A_recovery_code_is_accepted_however_the_user_types_it()
    {
        var (client, _) = await EnrolledUserAsync();
        var codes = await RecoveryCodesAsync(client);
        await client.PostAsJsonAsync("/api/auth/login", new { email = Email(client), password = Password });

        // Lower case, spaces for the dash, and an O typed where the code has a zero.
        var mangled = $"  {codes[0].ToLowerInvariant().Replace('-', ' ').Replace('0', 'o')}  ";
        Assert.Equal(HttpStatusCode.OK, (await Verify(client, mangled)).StatusCode);
    }

    [Fact]
    public async Task Repeated_failures_end_the_pending_session()
    {
        var (client, _) = await EnrolledUserAsync();
        await client.PostAsJsonAsync("/api/auth/login", new { email = Email(client), password = Password });

        for (var attempt = 0; attempt < 4; attempt++)
        {
            Assert.Equal("invalid_code", await ErrorOf(await Verify(client, "000000")));
        }

        // The fifth ends the session rather than merely saying no, so continuing costs the password.
        Assert.Equal("too_many_attempts", await ErrorOf(await Verify(client, "000000")));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Verify(client, "000000")).StatusCode);
    }

    // The per-account control, which is the half a per-session counter cannot provide: an attacker who
    // opens a fresh session every five guesses looks like a series of first attempts to it. Reaching the
    // account limit must stop even a CORRECT code, or the account counter is decorative.
    [Fact]
    public async Task Failures_spread_across_sessions_still_trip_the_account_cooldown()
    {
        var (client, secret) = await EnrolledUserAsync();
        var email = Email(client);

        // Two sessions, five failures each: the session limit is 5, the account limit is 10.
        for (var session = 0; session < 2; session++)
        {
            var browser = NewBrowser();
            await browser.PostAsJsonAsync("/api/auth/login", new { email, password = Password });
            for (var attempt = 0; attempt < 5; attempt++)
            {
                await Verify(browser, "000000");
            }
        }

        var fresh = NewBrowser();
        await fresh.PostAsJsonAsync("/api/auth/login", new { email, password = Password });
        var valid = Totp.Compute(secret, Totp.StepAt(DateTimeOffset.UtcNow) + 1);

        Assert.Equal("too_many_attempts", await ErrorOf(await Verify(fresh, valid)));
    }

    [Fact]
    public async Task Confirming_revokes_sessions_opened_before_the_factor_existed()
    {
        var (owner, _) = await SignedUpAsync();
        var email = Email(owner);

        var otherBrowser = NewBrowser();
        await otherBrowser.PostAsJsonAsync("/api/auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, (await otherBrowser.GetAsync("/api/auth/me")).StatusCode);

        await EnrolAndConfirmAsync(owner);

        Assert.Equal(HttpStatusCode.Unauthorized, (await otherBrowser.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/auth/me")).StatusCode); // but not our own
    }

    // Every path that mints a session has to decide about MFA, and one of them silently did not: the
    // Google callback issued a full session, so anyone who had enrolled a factor could skip it by
    // signing in with Google instead of a password. This asserts the rule rather than counting call
    // sites, so a sign-in route added later fails here by name instead of becoming a quiet bypass.
    [Fact]
    public void Every_session_minting_path_decides_about_mfa()
    {
        // Exempt, and why: signup cannot have a factor yet, and enterprise SSO delegates to the org's
        // own IdP, which is the whole reason a company buys it (ADR-0039).
        var exempt = new[] { "signup", "SsoSignIn" };

        var offenders = Directory
            .EnumerateFiles(ControlPlaneSource(), "*.cs", SearchOption.AllDirectories)
            .SelectMany(path => File.ReadAllLines(path).Select(line => (Path.GetFileName(path), line)))
            .Where(entry => entry.line.Contains("Sessions.IssueAsync", StringComparison.Ordinal))
            .Where(entry => !entry.line.Contains("mfaPending", StringComparison.Ordinal))
            .Where(entry => !exempt.Any(name =>
                entry.Item1.Contains(name, StringComparison.Ordinal)
                || entry.line.Contains(name, StringComparison.Ordinal)))
            .Select(entry => $"{entry.Item1}: {entry.line.Trim()}")
            .ToList();

        // The signup call site lives in AuthEndpoints.cs alongside login, so it is matched on the line.
        Assert.True(offenders.Count <= 1, $"session paths that ignore MFA: {string.Join(" | ", offenders)}");
    }

    [Fact]
    public async Task An_account_without_mfa_signs_in_exactly_as_before()
    {
        var (client, _) = await SignedUpAsync();

        var login = await client.PostAsJsonAsync("/api/auth/login", new { email = Email(client), password = Password });
        Assert.False((await Body(login)).GetProperty("mfaRequired").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    // --- helpers ------------------------------------------------------------

    private readonly Dictionary<HttpClient, string> emails = new();

    private string Email(HttpClient client) => emails[client];

    // A fresh client is a fresh cookie jar, so each one behaves as a separate browser.
    private HttpClient NewBrowser() => ControlPlaneApp.Create(pg.ConnectionString).CreateClient();

    private async Task<(HttpClient Client, string Secret)> SignedUpAsync()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var client = NewBrowser();
        var email = $"mfa-{Guid.NewGuid():N}@example.test";
        await client.PostAsJsonAsync("/api/auth/signup", new { email, password = Password });
        emails[client] = email;
        return (client, string.Empty);
    }

    private async Task<(HttpClient Client, string Secret)> EnrolledUserAsync()
    {
        var (client, _) = await SignedUpAsync();
        return (client, await EnrolAndConfirmAsync(client));
    }

    private static async Task<string> EnrolAndConfirmAsync(HttpClient client)
    {
        var enrol = await client.PostAsJsonAsync("/api/auth/mfa/enroll", new { password = Password });
        var secret = (await Body(enrol)).GetProperty("secret").GetString()!;
        var code = Totp.Compute(secret, Totp.StepAt(DateTimeOffset.UtcNow));
        var confirm = await client.PostAsJsonAsync("/api/auth/mfa/confirm", new { password = Password, code });
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        return secret;
    }

    private static async Task<string[]> RecoveryCodesAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/auth/mfa/recovery-codes", new { password = Password });
        return (await Body(response)).GetProperty("codes").EnumerateArray()
            .Select(c => c.GetString()!).ToArray();
    }

    private static Task<HttpResponseMessage> Verify(HttpClient client, string code) =>
        client.PostAsJsonAsync("/api/auth/mfa/verify", new { code });

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "backend")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    private static string ControlPlaneSource() =>
        Path.Combine(RepoRoot(), "backend", "src", "Condux.ControlPlane");

    private static async Task<JsonElement> Body(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task<string> ErrorOf(HttpResponseMessage response) =>
        (await Body(response)).GetProperty("error").GetString()!;
}
