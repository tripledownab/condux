using Condux.Core.Alerting;
using Xunit;

namespace Condux.Core.Tests;

/// <summary>
/// What a notification channel's delivery target may be, per channel. The three write paths that store one
/// used to accept any text that was not blank, so these cover both halves of the answer: that a transport
/// gets the kind of value it can actually use, and that a channel with no rule of its own is refused.
/// </summary>
public class ChannelTargetsTests
{
    [Theory]
    [InlineData(NotificationChannel.Webhook, "https://hooks.example.test/w")]
    [InlineData(NotificationChannel.Slack, "https://hooks.slack.com/services/T/B/x")]
    [InlineData(NotificationChannel.Discord, "https://discord.com/api/webhooks/1/x")]
    [InlineData(NotificationChannel.Webhook, "http://hooks.example.test/w")]
    public void AnHttpTransportTakesAnAbsoluteHttpUrl(NotificationChannel channel, string target)
    {
        Assert.True(ChannelTargets.IsValid(channel, target));
    }

    [Theory]
    [InlineData("hooks.example.test/w")]          // no scheme, so not an absolute URL
    [InlineData("//hooks.example.test/w")]        // scheme relative, resolves against whoever fetches it
    [InlineData("/api/admin/orgs")]               // a path on this deployment
    [InlineData("file:///etc/passwd")]
    [InlineData("gopher://127.0.0.1:5432/x")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ops@acme.test")]                 // an address is a target for a different channel
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void AnHttpTransportRefusesEverythingElse(string? target)
    {
        // Every one of these was accepted before, because the only check was that the value was not blank.
        // The overlap with HttpUrlsTests is deliberate: that class covers the predicate, these rows cover
        // the answer a webhook target gets, which stays true if the arm is ever pointed somewhere else.
        Assert.False(ChannelTargets.IsValid(NotificationChannel.Webhook, target));
    }

    [Theory]
    [InlineData("ops@acme.test", true)]
    [InlineData("https://hooks.example.test/w", false)]
    [InlineData("ops", false)]
    [InlineData("", false)]
    public void EmailTakesAnAddressAndNotAUrl(string target, bool expected)
    {
        Assert.Equal(expected, ChannelTargets.IsValid(NotificationChannel.Email, target));
    }

    [Fact]
    public void SurroundingWhitespaceDoesNotDecideTheAnswer()
    {
        // The write paths trim before storing, so a value that is only padded must not be refused here and
        // then accepted there.
        Assert.True(ChannelTargets.IsValid(NotificationChannel.Webhook, "  https://hooks.example.test/w  "));
        Assert.True(ChannelTargets.IsValid(NotificationChannel.Email, " ops@acme.test "));
    }

    [Fact]
    public void AChannelWithNoArmIsRefusedRatherThanWavedThrough()
    {
        // The wire carries an int, so an undefined value reaches this cast. It is also what a channel
        // added to the enum without a decision here would look like, and that must fail closed.
        Assert.False(ChannelTargets.IsValid((NotificationChannel)0, "https://hooks.example.test/w"));
        Assert.False(ChannelTargets.IsValid((NotificationChannel)99, "https://hooks.example.test/w"));
    }
}
