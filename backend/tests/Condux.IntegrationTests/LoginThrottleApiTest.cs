using System.Net;
using System.Net.Http.Json;
using Condux.ControlPlane.Auth;
using Condux.IntegrationTests.Fixtures;
using Condux.Storage.Postgres;
using Npgsql;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// The per-account password throttle, over HTTP, because that is the layer an attacker uses.
/// </summary>
/// <remarks>
/// Every test here asserts something that is FALSE without the throttle, rather than restating it. The
/// recurring shape is "and now the CORRECT password is refused": a counter that stops wrong passwords
/// and lets the right one through on the next attempt has not stopped anything, since guessing right is
/// the whole objective.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class LoginThrottleApiTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private const string Password = "correct-horse-battery";

    [Fact]
    public async Task Enough_wrong_passwords_refuse_the_right_one()
    {
        var (client, email) = await SignedUpAsync();

        for (var attempt = 0; attempt < PasswordGate.MaxAttempts; attempt++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(client, email, "not-it")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(client, email, Password)).StatusCode);
    }

    // The half a login-only throttle misses. Re-authenticating asks for the same password, so an attacker
    // holding a stolen cookie could grind it at /api/auth/password or /api/auth/mfa/enroll while the
    // sign-in page refused them. Two counters would hand out one allowance per surface.
    [Fact]
    public async Task The_two_surfaces_that_check_a_password_share_one_allowance()
    {
        var (signedIn, email) = await SignedUpAsync();

        // Burn the allowance at the sign-in page, from a different browser so the session survives.
        var attacker = NewBrowser();
        for (var attempt = 0; attempt < PasswordGate.MaxAttempts; attempt++)
        {
            await LoginAsync(attacker, email, "not-it");
        }

        // The CORRECT current password, on a live session, is refused: the count followed the account.
        Assert.Equal(HttpStatusCode.Unauthorized, (await ChangePasswordAsync(signedIn, Password)).StatusCode);
    }

    [Fact]
    public async Task Failures_while_re_authenticating_count_against_signing_in()
    {
        var (signedIn, email) = await SignedUpAsync();

        for (var attempt = 0; attempt < PasswordGate.MaxAttempts; attempt++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await ChangePasswordAsync(signedIn, "not-it")).StatusCode);
        }

        var fresh = NewBrowser();
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(fresh, email, Password)).StatusCode);
    }

    // Without this a user who mistypes a few times and then gets in stays one typo from a cooldown for
    // the rest of the window, which is the behaviour that makes people describe throttles as broken.
    [Fact]
    public async Task Signing_in_clears_the_count()
    {
        var (client, email) = await SignedUpAsync();

        for (var attempt = 0; attempt < PasswordGate.MaxAttempts - 1; attempt++)
        {
            await LoginAsync(client, email, "not-it");
        }

        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, email, Password)).StatusCode);

        // A full allowance again, so the last of these is the first to trip it, not the first of them.
        for (var attempt = 0; attempt < PasswordGate.MaxAttempts - 1; attempt++)
        {
            await LoginAsync(client, email, "not-it");
        }

        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, email, Password)).StatusCode);
    }

    // The property the counter is windowed FOR. A lifetime total passes every test above and then leaves
    // a real account permanently one typo from a fresh cooldown, because the stored count never comes
    // back down.
    //
    // This covers the second-factor counter as well, because both build the same statement. Until now
    // nothing covered either: the only other test naming a cooldown is
    // MfaApiTest.Failures_spread_across_sessions_still_trip_the_account_cooldown, which tests the trip.
    [Fact]
    public async Task A_lapsed_cooldown_starts_the_count_again_rather_than_resuming_at_the_limit()
    {
        var (client, email) = await SignedUpAsync();

        for (var attempt = 0; attempt < PasswordGate.MaxAttempts; attempt++)
        {
            await LoginAsync(client, email, "not-it");
        }

        await ExpireCooldownAsync(email);

        // One failure after the lapse must count as the FIRST, leaving the allowance intact.
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(client, email, "not-it")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, email, Password)).StatusCode);
    }

    // The row lock inside FailureCooldown.Record, which reads like noise and is not. A CTE is evaluated
    // against the snapshot taken when the statement began, so without the lock two overlapping failures
    // both compute the same total and the second overwrites the first. Measured against this statement on
    // PostgreSQL 16 before the lock went in: ten concurrent failures counted six, which is an attempt
    // allowance an attacker stretches simply by issuing guesses in parallel.
    //
    // One-sided by nature. Calls that happen not to overlap count correctly either way, so this can pass
    // when it should not, but it never fails when the lock is there.
    [Fact]
    public async Task Concurrent_failures_are_each_counted()
    {
        var (_, email) = await SignedUpAsync();
        var users = new UserRepository(pg.ConnectionString);
        var userId = await UserIdAsync(email);

        const int concurrent = 20;
        await Task.WhenAll(Enumerable.Range(0, concurrent).Select(_ =>
            users.RecordLoginFailureAsync(userId, PasswordGate.MaxAttempts, PasswordGate.Cooldown)));

        // Tripping the cooldown part way through does not stop the counting: the lapse branch only fires
        // once the cooldown is in the PAST, so all twenty must still land.
        Assert.Equal(concurrent, await FailedLoginsAsync(email));
    }

    // This one guards the DESIGN rather than the throttle: it would pass against no throttle at all. What
    // it refuses is the obvious alternative, counting by the submitted address in a table of its own,
    // which lets anyone hold an unregistered address hostage by failing to sign in as it.
    [Fact]
    public async Task An_address_with_no_account_is_refused_and_leaves_it_free_to_sign_up()
    {
        var client = NewBrowser();
        var email = $"throttle-{Guid.NewGuid():N}@example.test";

        for (var attempt = 0; attempt < PasswordGate.MaxAttempts + 1; attempt++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(client, email, "not-it")).StatusCode);
        }

        // Nothing was recorded against an address that matches no row, so the address is still usable.
        Assert.Equal(HttpStatusCode.OK,
            (await client.PostAsJsonAsync("/api/auth/signup", new { email, password = Password })).StatusCode);
    }

    // --- helpers ------------------------------------------------------------

    private HttpClient NewBrowser() => ControlPlaneApp.Create(pg.ConnectionString).CreateClient();

    private async Task<(HttpClient Client, string Email)> SignedUpAsync()
    {
        var client = NewBrowser();
        var email = $"throttle-{Guid.NewGuid():N}@example.test";
        var signup = await client.PostAsJsonAsync("/api/auth/signup", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, signup.StatusCode);
        return (client, email);
    }

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string email, string password) =>
        client.PostAsJsonAsync("/api/auth/login", new { email, password });

    private static Task<HttpResponseMessage> ChangePasswordAsync(HttpClient client, string current) =>
        client.PostAsJsonAsync("/api/auth/password",
            new { currentPassword = current, newPassword = "a-different-one-99" });

    /// <summary>
    /// Moves the account's cooldown into the past. The window is a quarter of an hour of wall clock, so
    /// the only alternatives are waiting for it or injecting a clock into a statement whose whole point is
    /// that it resolves the time in the database.
    /// </summary>
    private async Task ExpireCooldownAsync(string email)
    {
        await using var conn = new NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "UPDATE users SET login_locked_until = now() - interval '1 minute' WHERE email = @email;", conn);
        cmd.Parameters.AddWithValue("email", email);
        Assert.Equal(1, await cmd.ExecuteNonQueryAsync());
    }

    private Task<long> UserIdAsync(string email) => ScalarAsync<long>("id", email);

    private Task<int> FailedLoginsAsync(string email) => ScalarAsync<int>("failed_logins", email);

    /// <summary>
    /// Reads one column of the account's counter state. The repository deliberately does not expose the
    /// count, so a test that needs the number reads it here rather than the API growing a way to leak it.
    /// </summary>
    private async Task<T> ScalarAsync<T>(string column, string email)
    {
        await using var conn = new NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"SELECT {column} FROM users WHERE email = @email;", conn);
        cmd.Parameters.AddWithValue("email", email);
        return (T)(await cmd.ExecuteScalarAsync() ?? throw new InvalidOperationException($"no user {email}"));
    }
}
