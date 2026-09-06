using System.Net.Mail;
using Condux.Core.WeeklySummaries;
using Condux.Notifications;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace Condux.Notifications.Tests;

public class WeeklySummaryEmailTests
{
    private static WeeklySummary Sample(WeeklyFixActivity? fixes = null, IReadOnlyList<TopIssue>? top = null) => new(
        OrgId: 1, OrgName: "Acme",
        WeekStart: new DateTimeOffset(2024, 1, 1, 9, 0, 0, TimeSpan.Zero),
        WeekEnd: new DateTimeOffset(2024, 1, 8, 9, 0, 0, TimeSpan.Zero),
        Events: 18432, PreviousEvents: 15120,
        NewIssues: 12, Regressions: 3, Resolved: 8, OpenIssues: 41, UsersAffected: 1204,
        TopIssues: top ?? [new TopIssue(Guid.Parse("11111111-1111-1111-1111-111111111111"), "TypeError: undefined is not a function", 4231)],
        Fixes: fixes ?? new WeeklyFixActivity(5, 4, 2, 1));

    [Fact]
    public void Content_HasStatsFixAndTopIssueFacts_AndCtaWhenUrlGiven()
    {
        var content = WeeklySummaryEmail.Content(Sample(), "https://app.condux.ai");

        Assert.Equal("Your Condux week: Acme", content.Heading);
        Assert.Contains("18,432 events this week.", content.Paragraphs);
        Assert.NotNull(content.Facts);
        Assert.Contains(content.Facts!, f => f is { Label: "vs last week", Value: "+22%", ValueColorHex: not null });
        Assert.Contains(content.Facts!, f => f is { Label: "New issues", Value: "12" });
        Assert.Contains(content.Facts!, f => f is { Label: "Users affected", Value: "~1,204" });
        Assert.Contains(content.Facts!, f => f is { Label: "Fixes proposed", Value: "5" });
        Assert.Contains(content.Facts!, f => f is { Label: "1. TypeError: undefined is not a function", Value: "4,231 events" });
        Assert.NotNull(content.Button);
        Assert.Equal("https://app.condux.ai", content.Button!.Url);
    }

    [Fact]
    public void Content_OmitsFixSectionAndButton_WhenNoFixesAndNoUrl()
    {
        var content = WeeklySummaryEmail.Content(Sample(fixes: WeeklyFixActivity.None), dashboardUrl: null);
        Assert.DoesNotContain(content.Facts!, f => f.Label == "Fixes proposed");
        Assert.Null(content.Button);
    }

    [Fact]
    public void Content_TruncatesLongTopIssueTitles()
    {
        var longTitle = new string('x', 80);
        var content = WeeklySummaryEmail.Content(Sample(top: [new TopIssue(Guid.NewGuid(), longTitle, 10)]), null);
        var row = Assert.Single(content.Facts!, f => f.Label.StartsWith("1. "));
        Assert.True(row.Label.Length <= "1. ".Length + 44, $"label too long: {row.Label.Length}");
        Assert.EndsWith("…", row.Label);
    }

    [Fact]
    public void TrendFact_IsColoredByDirection_AndAbsentWithoutBaseline()
    {
        // Up = more errors = one color; down = fewer errors = the opposite color; no prior week = no trend fact.
        var up = Assert.Single(WeeklySummaryEmail.Content(Sample(), null).Facts!, f => f.Label == "vs last week");
        var down = Assert.Single(
            WeeklySummaryEmail.Content(Sample() with { Events = 80, PreviousEvents = 100 }, null).Facts!,
            f => f.Label == "vs last week");

        Assert.Equal("+22%", up.Value);
        Assert.Equal("-20%", down.Value);
        Assert.NotNull(up.ValueColorHex);
        Assert.NotNull(down.ValueColorHex);
        Assert.NotEqual(up.ValueColorHex, down.ValueColorHex);

        var noBaseline = WeeklySummaryEmail.Content(Sample() with { PreviousEvents = 0 }, null);
        Assert.DoesNotContain(noBaseline.Facts!, f => f.Label == "vs last week");
    }

    [Fact]
    public void RenderedHtml_ContainsHeadlineTopIssueAndCta_WithoutButtonArrow()
    {
        var html = EmailLayout.RenderHtml(WeeklySummaryEmail.Content(Sample(), "https://app.condux.ai"));
        Assert.Contains("Your Condux week: Acme", html);
        Assert.Contains("18,432 events this week", html);
        Assert.Contains("4,231 events", html);
        Assert.Contains("Open Condux", html);
        Assert.DoesNotContain("&rarr;", html); // the button arrow was removed
    }

    [Fact]
    public void TopIssueFact_LinksToTheIssue_AndOmitsTheLinkWithoutABaseUrl()
    {
        var id = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var linked = Assert.Single(
            WeeklySummaryEmail.Content(Sample(), "https://app.condux.ai").Facts!,
            f => f.Label.StartsWith("1. "));
        Assert.Equal($"https://app.condux.ai/issues/{id}", linked.LabelHref);

        // Without a base URL there is nothing to link to, and a relative path is useless in an email
        // client, so the row must render as plain text rather than a broken anchor.
        var unlinked = Assert.Single(
            WeeklySummaryEmail.Content(Sample(), dashboardUrl: null).Facts!,
            f => f.Label.StartsWith("1. "));
        Assert.Null(unlinked.LabelHref);
    }

    [Fact]
    public void RenderedHtml_WrapsALinkedTopIssueInAnAnchor_ButNotAnUnlinkedRow()
    {
        var id = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var html = EmailLayout.RenderHtml(WeeklySummaryEmail.Content(Sample(), "https://app.condux.ai"));
        Assert.Contains($"<a href=\"https://app.condux.ai/issues/{id}\"", html);

        // A metric row shares the same facts table and must not become a link.
        Assert.Contains(">New issues</td>", html);
    }

    [Fact]
    public void RenderedHtml_EncodesTheLabelHref()
    {
        // The href is attribute-encoded like every other value in the layout, so a quote in a URL cannot
        // break out of the attribute. Uses the layout directly, since a real issue URL never contains one.
        var content = new EmailContent(
            Heading: "h",
            Paragraphs: [],
            Facts: [new EmailFact("label", "value", null, "https://x.test/\"onmouseover=alert(1)")]);

        var html = EmailLayout.RenderHtml(content);

        Assert.DoesNotContain("\"onmouseover=", html);
        Assert.Contains("&quot;onmouseover=", html);
    }

    [Fact]
    public async Task Mailer_NoOp_WhenSmtpUnconfigured()
    {
        var mailer = new WeeklySummaryMailer([], [], new EmptyConfig(), NullLogger<WeeklySummaryMailer>.Instance);
        Assert.Equal(0, await mailer.SendAsync(Sample(), ["a@x.com"]));
    }

    [Fact]
    public async Task Mailer_SendsToEachNonBlankRecipient()
    {
        var options = new SmtpOptions("localhost", 25, "from@condux.dev", null, null, UseSsl: false);
        var sender = new RecordingSender();
        var mailer = new WeeklySummaryMailer(
            [sender], [options], new EmptyConfig(), NullLogger<WeeklySummaryMailer>.Instance);

        var sent = await mailer.SendAsync(Sample(), ["a@x.com", "  ", "b@x.com"]);

        Assert.Equal(2, sent);
        Assert.Equal(2, sender.Count);
        Assert.Equal("Your Condux week: Acme", sender.LastSubject);
    }

    private sealed class RecordingSender : ISmtpSender
    {
        public int Count { get; private set; }
        public string? LastSubject { get; private set; }

        public Task SendAsync(MailMessage message, CancellationToken cancellationToken = default)
        {
            Count++;
            LastSubject = message.Subject;
            return Task.CompletedTask;
        }
    }

    // Minimal IConfiguration returning nothing (so the mailer's dashboard URL resolves to null); avoids a
    // dependency on the concrete configuration builder in the test project.
    private sealed class EmptyConfig : IConfiguration
    {
        public string? this[string key] { get => null; set { } }
        public IEnumerable<IConfigurationSection> GetChildren() => [];
        public IChangeToken GetReloadToken() => throw new NotSupportedException();
        public IConfigurationSection GetSection(string key) => throw new NotSupportedException();
    }
}
