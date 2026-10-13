using Condux.Core.CveFix;
using Condux.Core.CveScanning;
using Condux.Core.FixEngine;
using Xunit;

namespace Condux.Core.Tests;

/// <summary>
/// The CVE bump verification policy (ADR-0051). Most cases use log4j-core's real advisory
/// GHSA-jfh8-c2jp-5v3q, which gives the package three ranges, because a version fixed under one range
/// can be vulnerable under another, and that is where a policy that read one range would go wrong.
/// </summary>
public class CveFixVerificationTests
{
    private const string Log4j = "org.apache.logging.log4j:log4j-core";

    private static readonly string[] Log4jRanges = [">= 2.13.0, < 2.15.0", ">= 2.4, < 2.12.2", ">= 2.0-beta9, < 2.3.1"];

    private static readonly DateTimeOffset MergedAt = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static ObservedModule Seen(string version, DateTimeOffset at, string package = Log4j, string ecosystem = "Maven") =>
        new(ecosystem, package, version, "production", "1.4.2", at);

    private static VerifyStatus Evaluate(
        TimeSpan sinceMerge, string target, params ObservedModule[] observed) =>
        CveFixVerification.Evaluate("maven", Log4j, target, Log4jRanges, MergedAt, MergedAt + sinceMerge, observed, complete: true).Status;

    [Fact]
    public void HoldsOnceTheFixedVersionRunsAndNothingVulnerableHasForTheWindow() =>
        Assert.Equal(VerifyStatus.Held, Evaluate(
            TimeSpan.FromDays(4), "2.15.0",
            Seen("2.15.0", MergedAt.AddDays(1)),
            Seen("2.14.1", MergedAt.AddHours(-1))));

    /// <summary>
    /// A read that stopped short may have left out a vulnerable sighting, so it never holds: it keeps
    /// watching, and at the deadline cannot be verified.
    /// </summary>
    [Fact]
    public void NeverHoldsOnAnIncompleteRead()
    {
        ObservedModule[] fixedOnly = [Seen("2.15.0", MergedAt.AddDays(1))];

        Assert.Equal(VerifyStatus.Watching, CveFixVerification.Evaluate(
            "maven", Log4j, "2.15.0", Log4jRanges, MergedAt, MergedAt.AddDays(4), fixedOnly, complete: false).Status);
        Assert.Equal(VerifyStatus.CannotVerify, CveFixVerification.Evaluate(
            "maven", Log4j, "2.15.0", Log4jRanges, MergedAt, MergedAt.AddDays(14), fixedOnly, complete: false).Status);
    }

    [Fact]
    public void KeepsWatchingBeforeTheWindowHasPassed() =>
        Assert.Equal(VerifyStatus.Watching, Evaluate(TimeSpan.FromDays(2), "2.15.0", Seen("2.15.0", MergedAt.AddDays(1))));

    [Fact]
    public void HoldsAtExactlyTheWindow() =>
        Assert.Equal(VerifyStatus.Held, Evaluate(TimeSpan.FromDays(3), "2.15.0", Seen("2.15.0", MergedAt.AddDays(1))));

    [Fact]
    public void DoesNotHoldWhileAVulnerableVersionIsStillReported() =>
        Assert.Equal(VerifyStatus.Watching, Evaluate(
            TimeSpan.FromDays(5), "2.15.0",
            Seen("2.15.0", MergedAt.AddDays(1)),
            Seen("2.14.1", MergedAt.AddDays(4))));

    /// <summary>A sighting stamped exactly at the start of the window is outside it.</summary>
    [Fact]
    public void ReadsTheWindowAsStrictlyAfterItsStart() =>
        Assert.Equal(VerifyStatus.Held, Evaluate(
            TimeSpan.FromDays(5), "2.15.0",
            Seen("2.15.0", MergedAt.AddDays(1)),
            Seen("2.14.1", MergedAt.AddDays(2))));

    /// <summary>
    /// A bump on the 2.12 line targets 2.12.2. A service on 2.13.5 is above that target but inside the
    /// advisory's first range, so it is vulnerable, not fixed, and the run is still exposed.
    /// </summary>
    [Fact]
    public void JudgesEverySightingAgainstEveryRangeOfTheAdvisory()
    {
        var verdict = CveFixVerification.Evaluate(
            "maven", Log4j, "2.12.2", Log4jRanges, MergedAt, MergedAt.AddDays(14),
            [Seen("2.12.2", MergedAt.AddDays(1)), Seen("2.13.5", MergedAt.AddDays(13))], complete: true);

        Assert.Equal(VerifyStatus.DidNotHold, verdict.Status);
        Assert.Equal(
            [SightingClass.Fixed, SightingClass.Vulnerable],
            verdict.Sightings.Select(sighting => sighting.Class));
    }

    /// <summary>
    /// Undecided is not fixed: this qualifier on 2.15.0's own numbers is read by no Maven rule here,
    /// so while it is reported the run cannot hold, and at the deadline it cannot be verified.
    /// </summary>
    [Fact]
    public void TreatsAnUndecidedVersionAsBlockingAndNeverAsFixed()
    {
        var undecided = Seen("2.15.0.redhat-00001", MergedAt.AddDays(13));

        Assert.Equal(VerifyStatus.Watching, Evaluate(TimeSpan.FromDays(5), "2.15.0", Seen("2.15.0", MergedAt.AddDays(1)), undecided));
        Assert.Equal(VerifyStatus.CannotVerify, Evaluate(TimeSpan.FromDays(14), "2.15.0", Seen("2.15.0", MergedAt.AddDays(1)), undecided));
    }

    [Fact]
    public void IsStillExposedAtTheDeadlineWhenAVulnerableVersionIsStillReported() =>
        Assert.Equal(VerifyStatus.DidNotHold, Evaluate(TimeSpan.FromDays(14), "2.15.0", Seen("2.14.1", MergedAt.AddDays(13))));

    /// <summary>A vulnerable version that stopped being reported before the window is not still exposed.</summary>
    [Fact]
    public void CannotVerifyAtTheDeadlineWhenTheVulnerableVersionStoppedBeingReported() =>
        Assert.Equal(VerifyStatus.CannotVerify, Evaluate(TimeSpan.FromDays(14), "2.15.0", Seen("2.14.1", MergedAt.AddDays(5))));

    /// <summary>
    /// "Fixed" needs both confirmations. Above a 2.12.2 target but undecided under the 2.13 line's
    /// range (every Maven puts this inside it), and outside every range but below a 2.15.0 target,
    /// are both undecided.
    /// </summary>
    [Theory]
    [InlineData("2.12.2", "2.13.0.redhat-00001")]
    [InlineData("2.15.0", "2.3.1")]
    public void ClassesAsFixedOnlyWhatIsConfirmedBothWays(string target, string version)
    {
        var verdict = CveFixVerification.Evaluate(
            "maven", Log4j, target, Log4jRanges, MergedAt, MergedAt.AddDays(4), [Seen(version, MergedAt.AddDays(1))], complete: true);

        Assert.Equal(SightingClass.Undecided, Assert.Single(verdict.Sightings).Class);
    }

    [Fact]
    public void CannotVerifyAtTheDeadlineWithNoInventory() =>
        Assert.Equal(VerifyStatus.CannotVerify, Evaluate(TimeSpan.FromDays(14), "2.15.0"));

    [Fact]
    public void KeepsWatchingWithNoInventoryUntilTheDeadline() =>
        Assert.Equal(VerifyStatus.Watching, Evaluate(TimeSpan.FromDays(13), "2.15.0"));

    /// <summary>
    /// GHSA-hrw6-wg82-cm62 names "jupyter_server"; an older release reports "jupyter-server", which
    /// PyPI reads as the same project. Still reported in staging, it keeps the run from holding.
    /// </summary>
    [Fact]
    public void CountsAPypiSightingSpelledWithOtherSeparators() =>
        Assert.Equal(
            VerifyStatus.Watching,
            CveFixVerification.Evaluate(
                "pip", "jupyter_server", "2.14.1", ["<= 2.14.0"], MergedAt, MergedAt.AddDays(5),
                [
                    Seen("2.14.1", MergedAt.AddDays(4), "jupyter_server", "PyPI"),
                    Seen("1.13.1", MergedAt.AddDays(4).AddHours(12), "jupyter-server", "PyPI") with { Environment = "staging" },
                ], complete: true).Status);

    /// <summary>
    /// A version vulnerable under only the advisory's second range is still vulnerable: 2.10.0 is
    /// outside the 2.13 line's range and inside "&gt;= 2.4, &lt; 2.12.2".
    /// </summary>
    [Fact]
    public void IsStillExposedUnderAnyOfTheRanges() =>
        Assert.Equal(VerifyStatus.DidNotHold, Evaluate(TimeSpan.FromDays(14), "2.15.0", Seen("2.10.0", MergedAt.AddDays(13))));

    /// <summary>A fixed version stamped exactly at the merge is not after it.</summary>
    [Fact]
    public void NeedsTheFixedVersionStrictlyAfterTheMerge() =>
        Assert.Equal(VerifyStatus.CannotVerify, Evaluate(TimeSpan.FromDays(14), "2.15.0", Seen("2.15.0", MergedAt)));

    /// <summary>A fixed version stamped before the merge says nothing about the merge.</summary>
    [Fact]
    public void NeedsTheFixedVersionReportedAfterTheMerge() =>
        Assert.Equal(VerifyStatus.CannotVerify, Evaluate(TimeSpan.FromDays(14), "2.15.0", Seen("2.15.0", MergedAt.AddHours(-1))));

    [Fact]
    public void CannotVerifyAtOnceInAnEcosystemWithNoScheme() =>
        Assert.Equal(
            VerifyStatus.CannotVerify,
            CveFixVerification.Evaluate("rubygems", "rack", "3.0.0", ["< 3.0.0"], MergedAt, MergedAt, [], complete: true).Status);

    /// <summary>With no ranges nothing can be outside all of them, so nothing is classed fixed either.</summary>
    [Fact]
    public void CannotVerifyAtOnceWithNoRanges()
    {
        var verdict = CveFixVerification.Evaluate("maven", Log4j, "2.15.0", [], MergedAt, MergedAt, [Seen("2.15.0", MergedAt.AddDays(1))], complete: true);

        Assert.Equal(VerifyStatus.CannotVerify, verdict.Status);
        Assert.Equal(SightingClass.Undecided, Assert.Single(verdict.Sightings).Class);
    }

    /// <summary>
    /// Only the bumped package counts, by its registry's name rule: npm's "Proto" is not "proto", so a
    /// vulnerable "proto" does not hold up a "Proto" bump, while PyPI's "django" is "Django".
    /// </summary>
    [Fact]
    public void CountsOnlySightingsOfTheBumpedPackage()
    {
        var npm = CveFixVerification.Evaluate(
            "npm", "Proto", "1.1.5", ["<= 1.1.4"], MergedAt, MergedAt.AddDays(4),
            [
                Seen("1.1.5", MergedAt.AddDays(1), "Proto", "npm"),
                Seen("1.0.0", MergedAt.AddDays(3), "proto", "npm"),
                Seen("1.0.0", MergedAt.AddDays(3), "Proto", "PyPI"),
            ], complete: true);
        var pip = CveFixVerification.Evaluate(
            "pip", "Django", "3.1.12", [">= 3.0, < 3.1.12"], MergedAt, MergedAt.AddDays(4),
            [Seen("3.1.12", MergedAt.AddDays(1), "django", "PyPI"), Seen("3.1.5", MergedAt.AddDays(3), "django", "PyPI")], complete: true);

        Assert.Equal(VerifyStatus.Held, npm.Status);
        Assert.Single(npm.Sightings);
        Assert.Equal(VerifyStatus.Watching, pip.Status);
        Assert.Equal(2, pip.Sightings.Count);
    }

    /// <summary>
    /// The ranges a bump is judged against: its own first, then the advisory's other ranges for the same
    /// package by the registry's rule, so GHSA-68w8-qjq3-2gfm's "django" and "Django" are one package on
    /// PyPI; another package, another ecosystem, a duplicate and a blank are dropped.
    /// </summary>
    [Fact]
    public void KeepsEveryRangeTheAdvisoryGivesThePackageItsOwnFirst()
    {
        AdvisoryRange[] advisory =
        [
            new("pip", "django", "< 2.2.24"),
            new("pip", "Django", ">= 3.0, < 3.1.12"),
            new("pip", "Django", ">= 3.2, < 3.2.4"),
            new("pip", "djangorestframework", "< 3.0"),
            new("npm", "django", "< 9.0.0"),
            new("pip", "django", " "),
        ];

        Assert.Equal(
            [">= 3.0, < 3.1.12", "< 2.2.24", ">= 3.2, < 3.2.4"],
            CveFixVerification.ToVulnerableRanges("pip", "Django", ">= 3.0, < 3.1.12", advisory));
    }

    [Fact]
    public void KeepsTheBumpsOwnRangeWhenTheAdvisoryListsNone() =>
        Assert.Equal(["< 4.17.21"], CveFixVerification.ToVulnerableRanges("npm", "lodash", "< 4.17.21", []));

    /// <summary>A job whose JSON carries the ranges as null, as no producer here writes, still becomes a run.</summary>
    [Fact]
    public void ReadsAJobWithNullRangesAsOneWithNone()
    {
        var job = System.Text.Json.JsonSerializer.Deserialize<CveFixJob>(
            """{"RepoLinkId":"00000000-0000-0000-0000-000000000001","RepoFullName":"acme/api","BaseBranch":"main","GhsaId":"GHSA-x","CveId":null,"Package":"lodash","Ecosystem":"npm","FromRange":"< 1.0.0","ToVersion":"1.0.0","AdvisoryUrl":"","Actor":"1","Model":"m","VulnerableRanges":null}""")!;

        Assert.Null(job.ToPendingRun("fake", MergedAt).VulnerableRanges);
    }

    /// <summary>
    /// Evidence is stored with each class as its name and read back by the same options, so what a
    /// person reads in the column and what the API returns cannot drift apart.
    /// </summary>
    [Fact]
    public void StoresEvidenceWithClassesByNameAndReadsItBack()
    {
        var evidence = new CveFixEvidence(
            MergedAt.AddDays(4), MergedAt.AddDays(1), MergedAt, "2.15.0", Log4jRanges, Complete: false, Reason: CannotVerifyReason.Incomplete,
            [new CveFixEvidenceSighting("2.14.1", "staging", "1.4.2", MergedAt.AddDays(3), SightingClass.Vulnerable, InWindow: true)]);

        var json = evidence.ToJson();

        Assert.Contains("\"class\":\"vulnerable\"", json, StringComparison.Ordinal);
        var back = CveFixEvidence.Parse(json)!;
        Assert.Equal(evidence.Target, back.Target);
        Assert.Equal(evidence.Ranges, back.Ranges);
        Assert.Equal(evidence.Sightings, back.Sightings);
        Assert.Equal(
            (evidence.EvaluatedAt, evidence.QuietSince, evidence.MergedAt, false, CannotVerifyReason.Incomplete),
            (back.EvaluatedAt, back.QuietSince, back.MergedAt, back.Complete, back.Reason));
        Assert.Contains("\"reason\":\"incomplete\"", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// The evidence says where the window for anything still vulnerable began and which sightings fell
    /// inside it, so the dashboard can name only what the verdict counted, and keeps the read's completeness as the read reported it.
    /// </summary>
    [Fact]
    public void RecordsTheWindowAndTheReadInTheEvidence()
    {
        var now = MergedAt.AddDays(14);
        var verdict = CveFixVerification.Evaluate(
            "maven", Log4j, "2.15.0", Log4jRanges, MergedAt, now,
            [Seen("2.14.1", now.AddDays(-1)), Seen("2.14.0", now - CveFixVerification.QuietWindow)], complete: false);

        var evidence = CveFixVerification.ToEvidence(verdict, "2.15.0", Log4jRanges, MergedAt, now, complete: false);

        Assert.Equal((now, now - CveFixVerification.QuietWindow, false), (evidence.EvaluatedAt, evidence.QuietSince, evidence.Complete));
        Assert.Equal([("2.14.1", true), ("2.14.0", false)], evidence.Sightings.Select(s => (s.Version, s.InWindow)));
    }

    /// <summary>A vulnerable sighting stamped exactly as the window opens is outside it.</summary>
    [Fact]
    public void CountsOnlyWhatWasStampedInsideTheWindow()
    {
        var now = MergedAt.AddDays(14);

        var verdict = CveFixVerification.Evaluate(
            "maven", Log4j, "2.15.0", Log4jRanges, MergedAt, now, [Seen("2.14.1", now - CveFixVerification.QuietWindow)], complete: true);

        Assert.Equal(VerifyStatus.CannotVerify, verdict.Status);
    }

    /// <summary>
    /// Evidence this version cannot read whole reads as none, so one run's column cannot fail the list of
    /// runs it is in: invalid JSON, the wrong shape, a missing field, and a class a newer conductor wrote.
    /// </summary>
    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("\"x\"")]
    [InlineData("{}")]
    [InlineData("""{"held":true}""")]
    [InlineData("""{"target":"1.0","ranges":["< 1.0"],"complete":true,"sightings":[]}""")]
    [InlineData("""{"evaluatedAt":"2026-10-05T00:00:00Z","quietSince":"2026-10-02T00:00:00Z","target":"1.0","ranges":["< 1.0"],"complete":true,"sightings":[]}""")]
    [InlineData("""{"quietSince":"2026-10-02T00:00:00Z","mergedAt":"2026-10-01T00:00:00Z","target":"1.0","ranges":["< 1.0"],"complete":true,"sightings":[]}""")]
    [InlineData("""{"evaluatedAt":"2026-10-05T00:00:00Z","mergedAt":"2026-10-01T00:00:00Z","target":"1.0","ranges":["< 1.0"],"complete":true,"sightings":[]}""")]
    [InlineData("""{"target":"1.0","ranges":["< 1.0"],"complete":true,"sightings":[{"version":"0.9","environment":"p","release":"r","lastSeen":"2026-10-01T00:00:00Z","class":"bogus"}]}""")]
    [InlineData("""{"target":"1.0","ranges":["< 1.0"],"complete":true,"sightings":[{"environment":"p","release":"r","lastSeen":"2026-10-01T00:00:00Z","class":"fixed"}]}""")]
    public void ReadsEvidenceItCannotReadWholeAsNone(string json) =>
        Assert.Null(CveFixEvidence.Parse(json));

    /// <summary>
    /// Cannot verify says why, from the first thing held needed that was missing, so the dashboard can
    /// explain it without restating the rule.
    /// </summary>
    [Theory]
    [InlineData("rubygems", true, true, CannotVerifyReason.NoScheme)]
    [InlineData("maven", false, true, CannotVerifyReason.NoRanges)]
    public void SaysWhyItCouldNotVerifyAtOnce(string ecosystem, bool withRanges, bool complete, CannotVerifyReason reason) =>
        Assert.Equal(reason, CveFixVerification.Evaluate(
            ecosystem, Log4j, "2.15.0", withRanges ? Log4jRanges : [], MergedAt, MergedAt, [], complete).Reason);

    [Fact]
    public void SaysWhyItCouldNotVerifyAtTheDeadline()
    {
        CannotVerifyReason? At(bool complete, params ObservedModule[] observed) =>
            CveFixVerification.Evaluate(
                "maven", Log4j, "2.15.0", Log4jRanges, MergedAt, MergedAt.AddDays(14), observed, complete).Reason;

        Assert.Equal(CannotVerifyReason.Incomplete, At(false, Seen("2.15.0", MergedAt.AddDays(1))));
        Assert.Equal(CannotVerifyReason.NothingReported, At(true));
        Assert.Equal(CannotVerifyReason.FixedNotReported, At(true, Seen("2.15.0.redhat-00001", MergedAt.AddDays(1))));
        Assert.Equal(
            CannotVerifyReason.UndecidedReported,
            At(true, Seen("2.15.0", MergedAt.AddDays(1)), Seen("2.15.0.redhat-00001", MergedAt.AddDays(13))));
    }

    /// <summary>Only Cannot verify carries a reason.</summary>
    [Fact]
    public void GivesNoReasonForAnyOtherVerdict()
    {
        Assert.Null(CveFixVerification.Evaluate(
            "maven", Log4j, "2.15.0", Log4jRanges, MergedAt, MergedAt.AddDays(4), [Seen("2.15.0", MergedAt.AddDays(1))], complete: true).Reason);
        Assert.Null(CveFixVerification.Evaluate(
            "maven", Log4j, "2.15.0", Log4jRanges, MergedAt, MergedAt.AddDays(14), [Seen("2.14.1", MergedAt.AddDays(13))], complete: true).Reason);
        Assert.Null(CveFixVerification.Evaluate(
            "maven", Log4j, "2.15.0", Log4jRanges, MergedAt, MergedAt.AddDays(2), [], complete: true).Reason);
    }
}
