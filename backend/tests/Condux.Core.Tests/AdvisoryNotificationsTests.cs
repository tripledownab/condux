using Condux.Core.Alerting;
using Condux.Core.CveScanning;
using Condux.Core.Events;
using Xunit;

namespace Condux.Core.Tests;

public class AdvisoryNotificationsTests
{
    private static CveFinding Finding(
        CveSeverity severity = CveSeverity.Moderate, string? fixedVersion = "7.5.2", string? manifest = "web/package-lock.json") =>
        new("GHSA-c2qf-rxjj-qqgw", "CVE-2022-25883", severity, "semver ReDoS", "semver", "npm", "< 7.5.2",
            fixedVersion, "https://github.com/acme/api/security/dependabot/20", "dependabot")
        {
            ManifestPath = manifest,
        };

    [Fact]
    public void The_notification_names_the_advisory_and_has_no_issue()
    {
        var notification = AdvisoryNotifications.ToNotification(7, Finding());

        Assert.Equal(7, notification.ProjectId);
        Assert.Equal(Guid.Empty, notification.IssuePublicId);
        Assert.Equal(AlertEventType.Advisory, notification.EventType);
        Assert.Equal("GHSA-c2qf-rxjj-qqgw: semver ReDoS", notification.Title);
        Assert.Equal(
            "npm semver < 7.5.2, fixed in 7.5.2, web/package-lock.json, https://github.com/acme/api/security/dependabot/20",
            notification.Culprit);
    }

    [Fact]
    public void An_advisory_with_no_fix_says_so_and_skips_what_it_does_not_know()
    {
        var notification = AdvisoryNotifications.ToNotification(7, Finding(fixedVersion: null, manifest: null));

        Assert.Equal(
            "npm semver < 7.5.2, no fixed version yet, https://github.com/acme/api/security/dependabot/20",
            notification.Culprit);
    }

    // The severity lands on the levels a rule already filters by, an unknown one on the default level of
    // an issue, so a rule that cannot place it still hears of it.
    [Theory]
    [InlineData(CveSeverity.Critical, Level.Fatal)]
    [InlineData(CveSeverity.High, Level.Error)]
    [InlineData(CveSeverity.Moderate, Level.Warning)]
    [InlineData(CveSeverity.Low, Level.Info)]
    [InlineData(CveSeverity.Unknown, Level.Error)]
    public void Severity_becomes_the_level(CveSeverity severity, Level level) =>
        Assert.Equal(level, AdvisoryNotifications.ToNotification(7, Finding(severity)).Level);
}
