using System.Net;
using System.Net.Http.Json;
using System.Net.Mail;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Condux.IntegrationTests.Fixtures;
using Condux.Notifications;
using Condux.Storage.Postgres;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// Forgotten-password recovery over HTTP. Before this there was none: no change-password for someone who
/// cannot sign in, and signup refuses an address that already exists, so one mistyped or forgotten
/// password lost the account and its email address for good.
///
/// The properties worth holding, in order of how quietly they break: the endpoint must not reveal which
/// addresses have accounts, a token must work exactly once, and a reset must not sign the user in, since
/// that would let mailbox access alone bypass a second factor.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PasswordResetApiTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    /// <summary>
    /// Captures what would have been mailed. The send is deliberately detached from the request (so a
    /// known address is not measurably slower than an unknown one), which means a test cannot read the
    /// list straight after the call. Awaiting a channel is the deterministic way to wait for it; polling
    /// with a sleep would be a race dressed up as a test.
    /// </summary>
    private sealed class RecordingSmtp : ISmtpSender
    {
        private readonly Channel<MailMessage> sent = Channel.CreateUnbounded<MailMessage>();

        public Task SendAsync(MailMessage message, CancellationToken cancellationToken = default)
        {
            // The message is disposed by the caller, so keep what the assertions need, not the object.
            sent.Writer.TryWrite(new MailMessage(
                "noreply@condux.test", message.To[0].Address, message.Subject, message.Body));
            return Task.CompletedTask;
        }

        /// <summary>The next message, or a failed assertion if none arrives. Never hangs a run.</summary>
        public async Task<MailMessage> NextAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                return await sent.Reader.ReadAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                Assert.Fail("Expected a reset email, none was sent.");
                throw;
            }
        }

        /// <summary>How many have arrived so far. Only meaningful after a send has been awaited.</summary>
        public int Count => sent.Reader.Count;
    }

    private static string UniqueEmail() => $"reset-{Guid.NewGuid():N}@condux.test";

    private (WebApplicationFactory<Program> App, RecordingSmtp Smtp) CreateApp()
    {
        var smtp = new RecordingSmtp();
        var app = ControlPlaneApp.Create(pg.ConnectionString, appBaseUrl: "https://app.condux.test")
            .WithWebHostBuilder(b =>
        {
            b.ConfigureTestServices(s =>
            {
                s.AddSingleton<ISmtpSender>(smtp);
                s.AddSingleton(new SmtpOptions("localhost", 25, "noreply@condux.test", null, null, false));
            });
        });
        return (app, smtp);
    }

    /// <summary>
    /// Reset rows minted for one address. Scoped to the address on purpose: the class shares one database
    /// across its tests, so counting the whole table would make each test's answer depend on which others
    /// had run.
    /// </summary>
    private async Task<int> ResetRowsAsync(string email)
    {
        await using var conn = new Npgsql.NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(
            """
            SELECT count(*) FROM password_resets r
            JOIN users u ON u.id = r.user_id
            WHERE u.email = @email;
            """, conn);
        cmd.Parameters.AddWithValue("email", email);
        return (int)(long)(await cmd.ExecuteScalarAsync())!;
    }

    private static string TokenFrom(MailMessage message) =>
        Regex.Match(message.Body, @"/reset\?token=([^\s]+)").Groups[1].Value;

    [Fact]
    public async Task Forgot_then_reset_replaces_the_password_and_the_link_works_only_once()
    {
        var (app, smtp) = CreateApp();
        var email = UniqueEmail();
        var client = app.CreateClient();
        await client.PostAsJsonAsync("/api/auth/signup", new { email, password = "forgotten-one-123" });

        var asked = await app.CreateClient().PostAsJsonAsync("/api/auth/password/forgot", new { email });
        Assert.Equal(HttpStatusCode.Accepted, asked.StatusCode);
        var token = Uri.UnescapeDataString(TokenFrom(await smtp.NextAsync()));
        Assert.NotEqual("", token);

        var reset = await app.CreateClient().PostAsJsonAsync("/api/auth/password/reset",
            new { token, newPassword = "brand-new-pass-456" });
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);

        // The credential really swapped, in both directions.
        var fresh = app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await fresh.PostAsJsonAsync("/api/auth/login",
                new { email, password = "forgotten-one-123" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await fresh.PostAsJsonAsync("/api/auth/login",
                new { email, password = "brand-new-pass-456" })).StatusCode);

        // Single use: the same link cannot be redeemed again, so a mail left in an inbox is spent.
        var replay = await app.CreateClient().PostAsJsonAsync("/api/auth/password/reset",
            new { token, newPassword = "third-password-789" });
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
    }

    /// <summary>
    /// Resetting must not sign the caller in. If it did, whoever reached the mailbox would be signed in
    /// without ever meeting the account's second factor, which is the one thing MFA exists to prevent.
    /// </summary>
    [Fact]
    public async Task Reset_does_not_sign_the_caller_in_and_ends_existing_sessions()
    {
        var (app, smtp) = CreateApp();
        var email = UniqueEmail();

        // An existing signed-in session, which the reset must end: the usual reason to reset is that
        // someone else may be holding one.
        var signedIn = app.CreateClient();
        await signedIn.PostAsJsonAsync("/api/auth/signup", new { email, password = "forgotten-one-123" });
        Assert.Equal(HttpStatusCode.OK, (await signedIn.GetAsync("/api/auth/me")).StatusCode);

        var resetter = app.CreateClient();
        await resetter.PostAsJsonAsync("/api/auth/password/forgot", new { email });
        var token = Uri.UnescapeDataString(TokenFrom(await smtp.NextAsync()));
        await resetter.PostAsJsonAsync("/api/auth/password/reset",
            new { token, newPassword = "brand-new-pass-456" });

        Assert.Equal(HttpStatusCode.Unauthorized, (await resetter.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await signedIn.GetAsync("/api/auth/me")).StatusCode);
    }

    /// <summary>
    /// Changing the password from Settings must kill any reset link already in flight. Someone who
    /// changes it because they suspect trouble would otherwise leave the attacker's emailed link live for
    /// the rest of its hour, able to overwrite the password they just chose and sign them out.
    /// </summary>
    [Fact]
    public async Task Changing_the_password_spends_a_reset_link_already_in_flight()
    {
        var (app, smtp) = CreateApp();
        var email = UniqueEmail();
        var owner = app.CreateClient();
        await owner.PostAsJsonAsync("/api/auth/signup", new { email, password = "original-pass-123" });

        // A reset is triggered by someone else, and the link reaches the inbox.
        await app.CreateClient().PostAsJsonAsync("/api/auth/password/forgot", new { email });
        var token = Uri.UnescapeDataString(TokenFrom(await smtp.NextAsync()));

        // The owner changes their password instead of using the link.
        var changed = await owner.PostAsJsonAsync("/api/auth/password",
            new { currentPassword = "original-pass-123", newPassword = "chosen-by-owner-456" });
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);

        // The outstanding link is spent, so it cannot undo that choice.
        var stale = await app.CreateClient().PostAsJsonAsync("/api/auth/password/reset",
            new { token, newPassword = "attacker-choice-789" });
        Assert.Equal(HttpStatusCode.BadRequest, stale.StatusCode);

        var fresh = app.CreateClient();
        Assert.Equal(HttpStatusCode.OK,
            (await fresh.PostAsJsonAsync("/api/auth/login",
                new { email, password = "chosen-by-owner-456" })).StatusCode);
    }

    /// <summary>
    /// The answer cannot depend on whether the address exists, or the endpoint becomes a way to enumerate
    /// customers. Same status, and nothing sent.
    /// </summary>
    [Fact]
    public async Task Forgot_answers_the_same_for_an_unknown_address_and_sends_nothing()
    {
        var (app, _) = CreateApp();
        var email = UniqueEmail();

        var unknown = await app.CreateClient().PostAsJsonAsync("/api/auth/password/forgot",
            new { email });
        var malformed = await app.CreateClient().PostAsJsonAsync("/api/auth/password/forgot",
            new { email = "not-an-email" });

        Assert.Equal(HttpStatusCode.Accepted, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, malformed.StatusCode);
        // No token was minted, so nothing could be mailed. Asserted on the row rather than the mailbox
        // because the row is written inside the request and the send is not.
        Assert.Equal(0, await ResetRowsAsync(email));
    }

    /// <summary>
    /// A reset mails an address a stranger typed, so an unthrottled endpoint is a spam relay pointed at
    /// our own sending reputation. Bounded per account, since the address is what receives the mail.
    /// </summary>
    [Fact]
    public async Task Forgot_stops_sending_after_a_few_requests_but_still_answers_the_same()
    {
        var (app, smtp) = CreateApp();
        var email = UniqueEmail();
        await app.CreateClient().PostAsJsonAsync("/api/auth/signup",
            new { email, password = "forgotten-one-123" });

        for (var attempt = 0; attempt < 6; attempt++)
        {
            var resp = await app.CreateClient().PostAsJsonAsync("/api/auth/password/forgot", new { email });
            Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        }

        Assert.Equal(3, await ResetRowsAsync(email));
    }

    /// <summary>
    /// A federated account has no password. Setting one through a reset would create a second way in that
    /// the organization's identity provider never approved and cannot revoke.
    /// </summary>
    [Fact]
    public async Task Forgot_sends_nothing_for_an_account_with_no_password()
    {
        var (app, smtp) = CreateApp();
        var email = UniqueEmail();
        await new UserRepository(pg.ConnectionString).CreateFederatedAsync(email);

        var resp = await app.CreateClient().PostAsJsonAsync("/api/auth/password/forgot", new { email });

        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        Assert.Equal(0, await ResetRowsAsync(email));
    }

    [Fact]
    public async Task Reset_refuses_an_unknown_token_or_a_password_under_the_minimum()
    {
        var (app, _) = CreateApp();

        Assert.Equal(HttpStatusCode.BadRequest,
            (await app.CreateClient().PostAsJsonAsync("/api/auth/password/reset",
                new { token = "not-a-real-token", newPassword = "brand-new-pass-456" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await app.CreateClient().PostAsJsonAsync("/api/auth/password/reset",
                new { token = "anything", newPassword = "short" })).StatusCode);
    }
}
