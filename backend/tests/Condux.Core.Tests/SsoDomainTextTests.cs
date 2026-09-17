using Condux.Core.Auth;
using Condux.Core.OrgNotifications;
using Xunit;

namespace Condux.Core.Tests;

/// <summary>
/// The two notices an org gets when its proved SSO domain claim starts failing, and then stops routing
/// (ADR-0043 slice 2).
///
/// Both carry a date and they are not the same date, which is the thing worth pinning. One tells the org
/// when its sign-in breaks, the other when the record went missing. Swapping them, or rendering one from
/// the other's instant, produces a message that still reads perfectly and is wrong by exactly the grace
/// period.
/// </summary>
public class SsoDomainTextTests
{
    private static readonly DateTimeOffset LostAt = new(2026, 3, 1, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public void RecordMissing_NamesTheDateRoutingStops_NotTheDateItWentMissing()
    {
        var notice = SsoDomainText.RecordMissing("Acme", "acme.test", LostAt);

        Assert.Contains("acme.test", notice.Subject);
        Assert.Contains("Acme", notice.Body);
        // 1 March plus the seven-day grace. Written out rather than computed from VerificationGrace, so
        // changing that period has to be a deliberate edit here instead of a test that agrees with
        // whatever the code now says.
        Assert.Contains("8 March 2026", notice.Body);
        Assert.DoesNotContain("1 March 2026", notice.Body);
    }

    [Fact]
    public void RecordMissing_SaysWhatTheReaderLosesAndHowToStopIt()
    {
        var body = SsoDomainText.RecordMissing("Acme", "acme.test", LostAt).Body;

        // The consequence is not a degraded login. A member who only ever used the IdP is a federated
        // account and holds no password, so they lose access outright. If that sentence goes, the notice
        // reads as a routine warning and gets filed.
        Assert.Contains("no password to fall back on", body);
        Assert.Contains("Republish the record", body);
    }

    [Fact]
    public void VerificationLost_NamesTheDateItWentMissing_AndSaysSignInHasStopped()
    {
        var notice = SsoDomainText.VerificationLost("Acme", "acme.test", LostAt);

        Assert.Contains("has stopped", notice.Subject);
        Assert.Contains("1 March 2026", notice.Body);
        Assert.DoesNotContain("8 March 2026", notice.Body);
        Assert.Contains("no longer routes", notice.Body);
        Assert.Contains("Verify", notice.Body); // the way back is named, not implied
    }

    [Fact]
    public void LapsesAt_IsTheGracePeriodAfterTheRecordWentMissing()
    {
        Assert.Equal(LostAt + DomainVerification.VerificationGrace, DomainVerification.LapsesAt(LostAt));
        // The grace has to be long enough to survive a weekend and short enough that a domain which
        // changed hands stops routing within the month. Both ends, because a one-sided bound would pass
        // for a value of zero.
        Assert.InRange(DomainVerification.VerificationGrace, TimeSpan.FromDays(3), TimeSpan.FromDays(30));
    }

    // Escaped rather than written out, because the house-style gate reads the lines a branch adds and
    // would otherwise fire on the test that enforces it.
    private const char EmDash = '\u2014';

    [Fact]
    public void Copy_HasNoEmDash() // house style
    {
        foreach (var notice in new[]
        {
            SsoDomainText.RecordMissing("Acme", "acme.test", LostAt),
            SsoDomainText.VerificationLost("Acme", "acme.test", LostAt),
        })
        {
            Assert.DoesNotContain(EmDash, notice.Subject);
            Assert.DoesNotContain(EmDash, notice.Body);
        }
    }
}
