using System.Net;
using System.Net.Mail;
using System.Text.Json;
using Condux.Core.Alerting;
using Condux.Core.Events;
using Condux.Notifications;
using Xunit;

namespace Condux.Notifications.Tests;

public class NotifierTests
{
    private static readonly AlertChannel Target = new(Guid.NewGuid(), Guid.NewGuid(), NotificationChannel.Webhook, "");

    private static AlertNotification Notification(AlertEventType eventType = AlertEventType.NewIssue) =>
        new(7, Guid.Parse("0192f000-0000-7000-8000-000000000000"), "TypeError: boom", "app.py", Level.Error,
            eventType, "acme");

    // Captures the one outbound request and returns 200, so a notifier's wire shape can be asserted.
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    [Fact]
    public async Task Webhook_PostsMachineReadableJsonToTarget()
    {
        var handler = new CapturingHandler();
        var notifier = new WebhookNotifier(new StubHttpClientFactory(handler));
        var channel = Target with { Channel = NotificationChannel.Webhook, Target = "https://hooks.test/w" };

        await notifier.SendAsync(channel, Notification(AlertEventType.Regression));

        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("https://hooks.test/w", handler.Request.RequestUri!.ToString());
        using var doc = JsonDocument.Parse(handler.Body!);
        var root = doc.RootElement;
        Assert.Equal("regression", root.GetProperty("trigger").GetString());
        Assert.Equal("Error", root.GetProperty("level").GetString());
        Assert.Equal("TypeError: boom", root.GetProperty("title").GetString());
        Assert.Equal("app.py", root.GetProperty("culprit").GetString());
        Assert.Equal("acme", root.GetProperty("project").GetString());
    }

    [Fact]
    public async Task Slack_PostsTextPayloadToTarget()
    {
        var handler = new CapturingHandler();
        var notifier = new SlackNotifier(new StubHttpClientFactory(handler));
        var channel = Target with { Channel = NotificationChannel.Slack, Target = "https://hooks.slack.test/s" };

        await notifier.SendAsync(channel, Notification());

        Assert.Equal("https://hooks.slack.test/s", handler.Request!.RequestUri!.ToString());
        using var doc = JsonDocument.Parse(handler.Body!);
        var text = doc.RootElement.GetProperty("text").GetString();
        Assert.Contains("New issue", text);
        Assert.Contains("TypeError: boom", text);
    }

    [Fact]
    public async Task Webhook_SendMessage_PostsSubjectAndBody()
    {
        var handler = new CapturingHandler();
        var notifier = new WebhookNotifier(new StubHttpClientFactory(handler));

        await notifier.SendMessageAsync("https://hooks.test/w", "Paused", "Cap reached.");

        Assert.Equal("https://hooks.test/w", handler.Request!.RequestUri!.ToString());
        using var doc = JsonDocument.Parse(handler.Body!);
        Assert.Equal("Paused", doc.RootElement.GetProperty("subject").GetString());
        Assert.Equal("Cap reached.", doc.RootElement.GetProperty("body").GetString());
    }

    [Fact]
    public async Task Slack_SendMessage_PostsSubjectAndBodyAsText()
    {
        var handler = new CapturingHandler();
        var notifier = new SlackNotifier(new StubHttpClientFactory(handler));

        await notifier.SendMessageAsync("https://hooks.slack.test/s", "Paused", "Cap reached.");

        using var doc = JsonDocument.Parse(handler.Body!);
        var text = doc.RootElement.GetProperty("text").GetString();
        Assert.Contains("Paused", text);
        Assert.Contains("Cap reached.", text);
    }

    [Fact]
    public async Task Discord_PostsContentPayloadToTarget()
    {
        var handler = new CapturingHandler();
        var notifier = new DiscordNotifier(new StubHttpClientFactory(handler));
        var channel = Target with { Channel = NotificationChannel.Discord, Target = "https://discord.test/webhooks/x" };

        await notifier.SendAsync(channel, Notification());

        Assert.Equal("https://discord.test/webhooks/x", handler.Request!.RequestUri!.ToString());
        using var doc = JsonDocument.Parse(handler.Body!);
        var content = doc.RootElement.GetProperty("content").GetString();
        Assert.Contains("New issue", content);
        Assert.Contains("TypeError: boom", content);
    }

    [Fact]
    public async Task Discord_SendMessage_PostsContent()
    {
        var handler = new CapturingHandler();
        var notifier = new DiscordNotifier(new StubHttpClientFactory(handler));

        await notifier.SendMessageAsync("https://discord.test/webhooks/x", "Paused", "Cap reached.");

        using var doc = JsonDocument.Parse(handler.Body!);
        var content = doc.RootElement.GetProperty("content").GetString();
        Assert.Contains("Paused", content);
        Assert.Contains("Cap reached.", content);
    }

    [Fact]
    public async Task Email_SendMessage_ComposesSubjectBodyAndRecipient()
    {
        var sender = new CapturingSmtpSender();
        var options = new SmtpOptions("smtp.test", 587, "alerts@condux.test", null, null, UseSsl: true);
        var notifier = new EmailNotifier(sender, options);

        await notifier.SendMessageAsync("ops@acme.test", "Paused", "Cap reached.");

        var message = sender.Message!;
        Assert.Equal("alerts@condux.test", message.From!.Address);
        Assert.Equal("ops@acme.test", Assert.Single(message.To).Address);
        Assert.Equal("Paused", message.Subject);
        Assert.Equal("Cap reached.", message.Body);

        var html = sender.HtmlBody!;
        Assert.Contains("<html", html);
        Assert.Contains("Paused", html);
        Assert.Contains("Cap reached.", html);
    }

    [Fact]
    public async Task Slack_RendersTheChannelsCustomTemplate()
    {
        var handler = new CapturingHandler();
        var notifier = new SlackNotifier(new StubHttpClientFactory(handler));
        var channel = Target with
        {
            Channel = NotificationChannel.Slack,
            Target = "https://hooks.slack.test/s",
            Template = "{{level}}! {{title}}",
        };

        await notifier.SendAsync(channel, Notification());

        using var doc = JsonDocument.Parse(handler.Body!);
        Assert.Equal("Error! TypeError: boom", doc.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public async Task Slack_SendsTemplateMarkdownVerbatim()
    {
        // Slack renders its own mrkdwn on the text field (*bold* is a SINGLE asterisk, <url|label> links),
        // so we must pass the template's markup through unchanged for Slack to format it. Verified against
        // docs.slack.dev/messaging/formatting-message-text (2026-07).
        var handler = new CapturingHandler();
        var notifier = new SlackNotifier(new StubHttpClientFactory(handler));
        var channel = Target with
        {
            Channel = NotificationChannel.Slack,
            Target = "https://hooks.slack.test/s",
            Template = "*{{title}}* <https://x|open>",
        };

        await notifier.SendAsync(channel, Notification());

        using var doc = JsonDocument.Parse(handler.Body!);
        Assert.Equal("*TypeError: boom* <https://x|open>", doc.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public async Task Discord_SendsTemplateMarkdownVerbatim()
    {
        // Discord renders **bold** and [label](url) masked links in webhook content, so the template markup
        // rides the content field untouched. Verified against Discord's markdown docs (2026-07).
        var handler = new CapturingHandler();
        var notifier = new DiscordNotifier(new StubHttpClientFactory(handler));
        var channel = Target with
        {
            Channel = NotificationChannel.Discord,
            Target = "https://discord.test/webhooks/x",
            Template = "**{{title}}** [open](https://x)",
        };

        await notifier.SendAsync(channel, Notification());

        using var doc = JsonDocument.Parse(handler.Body!);
        Assert.Equal(
            "**TypeError: boom** [open](https://x)",
            doc.RootElement.GetProperty("content").GetString());
    }

    [Fact]
    public async Task Slack_NeutralizesBroadcastMentionsFromErrorText()
    {
        // An untrusted title carrying Slack broadcast or mention syntax must not ping the channel, and an
        // incoming webhook has no flag to suppress it. Slack decodes &amp;, &lt; and &gt; back for display
        // and nothing else, so replacing those three shows the text as written. The template's own link
        // still renders, which is the point of escaping the value rather than the finished string.
        var handler = new CapturingHandler();
        var notifier = new SlackNotifier(new StubHttpClientFactory(handler));
        var channel = Target with
        {
            Channel = NotificationChannel.Slack,
            Target = "https://hooks.slack.test/s",
            Template = "{{title}} <https://x|open>",
        };
        var pinging = Notification() with { Title = "boom <!channel> <@U123>" };

        await notifier.SendAsync(channel, pinging);

        using var doc = JsonDocument.Parse(handler.Body!);
        Assert.Equal(
            "boom &lt;!channel&gt; &lt;@U123&gt; <https://x|open>",
            doc.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public async Task Slack_ErrorTextCannotBecomeALinkWhoseLabelLies()
    {
        // The reported defect. Whoever can send an event picks the title, so a title spelling a Slack link
        // used to arrive in someone else's workspace as a live link with a label claiming anything. The
        // old guard escaped only a < followed by ! or @, which is exactly the two forms this is not.
        var handler = new CapturingHandler();
        var notifier = new SlackNotifier(new StubHttpClientFactory(handler));
        var channel = Target with
        {
            Channel = NotificationChannel.Slack,
            Target = "https://hooks.slack.test/s",
        };
        var phishing = Notification() with
        {
            Title = "<https://elsewhere.test/reset|Reset your Condux password>",
        };

        await notifier.SendAsync(channel, phishing);

        using var doc = JsonDocument.Parse(handler.Body!);
        var text = doc.RootElement.GetProperty("text").GetString()!;
        Assert.Contains("&lt;https://elsewhere.test/reset|Reset your Condux password&gt;", text);
        Assert.DoesNotContain("<https://elsewhere.test", text);
    }

    [Fact]
    public async Task Slack_SendMessage_EscapesTheSubjectAndBody()
    {
        // The org notice path rather than the alert path, and it carries untrusted text too: a Conductor
        // pause notice names the org, and an org's name is whatever someone typed. The bold marks are ours
        // and are added after the escape, so they still render.
        var handler = new CapturingHandler();
        var notifier = new SlackNotifier(new StubHttpClientFactory(handler));

        await notifier.SendMessageAsync(
            "https://hooks.slack.test/s", "Paused for <!channel> & co", "Org <@U123> reached its cap");

        using var doc = JsonDocument.Parse(handler.Body!);
        Assert.Equal(
            "*Paused for &lt;!channel&gt; &amp; co*\nOrg &lt;@U123&gt; reached its cap",
            doc.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public async Task Slack_EscapesTheAmpersandBeforeTheAngleBrackets()
    {
        // Order is load-bearing: replacing < first and & after would rewrite the entities just produced,
        // so &lt; would go out as &amp;lt; and Slack would display the entity instead of the character.
        var handler = new CapturingHandler();
        var notifier = new SlackNotifier(new StubHttpClientFactory(handler));
        var channel = Target with
        {
            Channel = NotificationChannel.Slack,
            Target = "https://hooks.slack.test/s",
        };

        await notifier.SendAsync(channel, Notification() with { Title = "a & b <c>" });

        using var doc = JsonDocument.Parse(handler.Body!);
        var text = doc.RootElement.GetProperty("text").GetString()!;
        Assert.Contains("a &amp; b &lt;c&gt;", text);
        Assert.DoesNotContain("&amp;lt;", text);
    }

    [Fact]
    public async Task Discord_SuppressesMentions()
    {
        var handler = new CapturingHandler();
        var notifier = new DiscordNotifier(new StubHttpClientFactory(handler));
        var channel = Target with { Channel = NotificationChannel.Discord, Target = "https://discord.test/webhooks/x" };

        await notifier.SendAsync(channel, Notification());

        using var doc = JsonDocument.Parse(handler.Body!);
        var parse = doc.RootElement.GetProperty("allowed_mentions").GetProperty("parse");
        Assert.Equal(0, parse.GetArrayLength()); // parse: [] -> error text can't ping @everyone/@here/roles
    }

    [Fact]
    public async Task Discord_SendMessage_SuppressesMentionsToo()
    {
        // The org notice path had no such guard while the alert path did, so an org name spelling a
        // mention reached a Discord channel as one.
        var handler = new CapturingHandler();
        var notifier = new DiscordNotifier(new StubHttpClientFactory(handler));

        await notifier.SendMessageAsync("https://discord.test/webhooks/x", "Paused", "@everyone cap reached");

        using var doc = JsonDocument.Parse(handler.Body!);
        var parse = doc.RootElement.GetProperty("allowed_mentions").GetProperty("parse");
        Assert.Equal(0, parse.GetArrayLength());
    }

    [Fact]
    public async Task Webhook_ThrowsOnNonSuccess()
    {
        var notifier = new WebhookNotifier(new StubHttpClientFactory(new ErrorHandler()));
        var channel = Target with { Target = "https://hooks.test/w" };
        await Assert.ThrowsAsync<HttpRequestException>(() => notifier.SendAsync(channel, Notification()));
    }

    private sealed class ErrorHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
    }

    private sealed class CapturingSmtpSender : ISmtpSender
    {
        public MailMessage? Message { get; private set; }
        public string? HtmlBody { get; private set; }

        // Captures the HTML alternate view while the message is alive (the notifier disposes it after send).
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

    [Fact]
    public async Task Email_ComposesSubjectBodyAndRecipient()
    {
        var sender = new CapturingSmtpSender();
        var options = new SmtpOptions("smtp.test", 587, "alerts@condux.test", null, null, UseSsl: true);
        var notifier = new EmailNotifier(sender, options);
        var channel = Target with { Channel = NotificationChannel.Email, Target = "ops@acme.test" };

        await notifier.SendAsync(channel, Notification());

        var message = sender.Message!;
        Assert.Equal("alerts@condux.test", message.From!.Address);
        Assert.Equal("ops@acme.test", Assert.Single(message.To).Address);
        Assert.Contains("New issue", message.Subject);
        Assert.Contains("TypeError: boom", message.Subject);
        Assert.Contains("app.py", message.Body);

        var html = sender.HtmlBody!;
        Assert.Contains("<html", html);
        Assert.Contains("Condux", html);
        Assert.Contains("TypeError: boom", html);
        Assert.Contains("New issue", html);
        Assert.Contains("app.py", html);
    }

    [Fact]
    public async Task Email_HtmlView_EncodesErrorDerivedContent()
    {
        var sender = new CapturingSmtpSender();
        var options = new SmtpOptions("smtp.test", 587, "alerts@condux.test", null, null, UseSsl: true);
        var notifier = new EmailNotifier(sender, options);
        var channel = Target with { Channel = NotificationChannel.Email, Target = "ops@acme.test" };
        var notification = new AlertNotification(
            7, Guid.NewGuid(), "<script>alert(1)</script>", "app.py", Level.Error, AlertEventType.NewIssue);

        await notifier.SendAsync(channel, notification);

        var html = sender.HtmlBody!;
        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }
}
