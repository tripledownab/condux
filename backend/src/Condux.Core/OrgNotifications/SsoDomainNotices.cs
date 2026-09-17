using Condux.Core.Auth;

namespace Condux.Core.OrgNotifications;

/// <summary>What to put in an org-facing notice. Subject and body travel together because every caller
/// needs both, and <c>OrgNotificationDispatcher</c> delivers them as a pair.</summary>
public sealed record OrgNotice(string Subject, string Body);

/// <summary>
/// The org-facing text for the two things that can happen to a proved SSO domain claim on re-check
/// (ADR-0043 slice 2): its record stopped resolving, and then the grace period ran out. Pure and
/// side-effect free, like <c>ConductorPauseText</c>, so one text reaches every channel.
///
/// Deliberately NOT keyed on an enum the way that one is. Its reason is a persisted column, and its two
/// cases carry the same facts. These two carry different ones, a deadline and a start date, so a single
/// signature would have to take a parameter that is meaningless in half the calls.
///
/// Both say what the consequence is for a person rather than for a row. A member who has only ever signed
/// in through the IdP is a federated account, which holds no password, so a lapsed claim is not a
/// degraded login for them. It is no login at all. House style: no em-dashes.
/// </summary>
public static class SsoDomainText
{
    /// <summary>Sent once, the day the record stops resolving. It names the date routing stops, because
    /// that is the fact the reader has to act on.</summary>
    public static OrgNotice RecordMissing(string orgName, string domain, DateTimeOffset lostAt)
    {
        var deadline = Day(DomainVerification.LapsesAt(lostAt));
        return new OrgNotice(
            $"Condux: the DNS record proving {domain} is missing",
            $"Condux could not find the TXT record that proves {orgName} controls {domain}. "
            + $"Single sign-on for that domain keeps working until {deadline}. "
            + "Republish the record and the next daily check picks it up, with nothing to click. "
            + $"If it is still missing on {deadline}, sign-in through your identity provider stops for "
            + "this domain. "
            + "Members who have only ever signed in that way hold no password to fall back on. "
            + "The record to publish is on the Settings page, under SSO.");
    }

    /// <summary>Sent once, when the grace period runs out and the claim stops routing. An enterprise whose
    /// SSO stopped with no message would experience it as an outage with no cause.</summary>
    public static OrgNotice VerificationLost(string orgName, string domain, DateTimeOffset lostAt) => new(
        $"Condux: single sign-on for {domain} has stopped",
        $"The TXT record proving {orgName} controls {domain} has been missing since {Day(lostAt)}. "
        + "Single sign-on for that domain no longer routes to your identity provider. "
        + "Members who signed in that way cannot sign in until it is restored. "
        + "Publish the record shown on the Settings page under SSO, then choose Verify.");

    // Invariant rather than the reader's locale: this text goes to a webhook and a chat channel as often
    // as to a person, and the org's members are not in one place anyway.
    private static string Day(DateTimeOffset instant) =>
        instant.UtcDateTime.ToString("d MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
}
