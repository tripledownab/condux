using System.Net.Mail;
using Condux.ControlPlane.Invites;
using Condux.Notifications;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Condux.ControlPlane.Tests;

public class InviteMailerTests
{
    private static readonly SmtpOptions Options =
        new("smtp.test", 587, "invites@condux.test", null, null, UseSsl: true);

    private sealed class CapturingSmtpSender : ISmtpSender
    {
        public MailMessage? Message { get; private set; }
        public string? HtmlBody { get; private set; }

        // Captures the HTML alternate view while the message is alive (the mailer disposes it after send).
        public Task SendAsync(MailMessage message, CancellationToken cancellationToken = default)
        {
            Message = message;
            var view = message.AlternateViews.SingleOrDefault(v => v.ContentType.MediaType == "text/html");
            if (view is not null)
            {
                view.ContentStream.Position = 0;
                using var reader = new StreamReader(view.ContentStream);
                HtmlBody = reader.ReadToEnd();
            }
            return Task.CompletedTask;
        }
    }

    private static IConfiguration Config(params (string Key, string Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.Select(e => new KeyValuePair<string, string?>(e.Key, e.Value)))
            .Build();

    private static InviteMailer Mailer(ISmtpSender? sender, IConfiguration config) => new(
        sender is null ? [] : [sender],
        sender is null ? [] : [Options],
        config,
        NullLogger<InviteMailer>.Instance);

    [Fact]
    public async Task Sends_an_email_with_an_absolute_accept_link()
    {
        var sender = new CapturingSmtpSender();
        var mailer = Mailer(sender, Config(("CONDUX_APP_BASE_URL", "https://app.condux.ai")));

        var sent = await mailer.SendAsync("test@thing.se", "Acme", "boss@acme.io", "member", "tok123");

        Assert.True(sent);
        Assert.NotNull(sender.Message);
        Assert.Equal("test@thing.se", sender.Message!.To.Single().Address);
        Assert.Equal("invites@condux.test", sender.Message.From!.Address);
        Assert.Contains("Acme", sender.Message.Subject);
        Assert.Contains("https://app.condux.ai/invite?token=tok123", sender.Message.Body);
        Assert.Contains("member", sender.Message.Body);
        Assert.Contains("boss@acme.io", sender.Message.Body);

        var html = sender.HtmlBody!;
        Assert.Contains("Accept invitation", html);
        Assert.Contains("href=\"https://app.condux.ai/invite?token=tok123\"", html);
        Assert.Contains("Acme", html);
    }

    [Fact]
    public async Task Url_encodes_the_token_in_the_link()
    {
        var sender = new CapturingSmtpSender();
        var mailer = Mailer(sender, Config(("CONDUX_APP_BASE_URL", "https://app.condux.ai")));

        await mailer.SendAsync("test@thing.se", "Acme", "boss@acme.io", "member", "tok+en/x");

        Assert.Contains("token=tok%2Ben%2Fx", sender.Message!.Body);
    }

    [Fact]
    public async Task Falls_back_to_the_first_cors_origin_when_no_app_base_url()
    {
        var sender = new CapturingSmtpSender();
        var mailer = Mailer(sender, Config(("CONDUX_CORS_ORIGINS", "http://localhost:3000,http://other")));

        var sent = await mailer.SendAsync("test@thing.se", "Acme", "boss@acme.io", "admin", "tok123");

        Assert.True(sent);
        Assert.Contains("http://localhost:3000/invite?token=tok123", sender.Message!.Body);
    }

    [Fact]
    public async Task Is_a_no_op_when_smtp_is_not_configured()
    {
        var mailer = Mailer(sender: null, Config(("CONDUX_APP_BASE_URL", "https://app.condux.ai")));

        var sent = await mailer.SendAsync("test@thing.se", "Acme", "boss@acme.io", "member", "tok123");

        Assert.False(sent);
    }

    [Fact]
    public async Task Is_a_no_op_when_no_absolute_base_url_is_available()
    {
        var sender = new CapturingSmtpSender();
        var mailer = Mailer(sender, Config());

        var sent = await mailer.SendAsync("test@thing.se", "Acme", "boss@acme.io", "member", "tok123");

        Assert.False(sent);
        Assert.Null(sender.Message);
    }
}
