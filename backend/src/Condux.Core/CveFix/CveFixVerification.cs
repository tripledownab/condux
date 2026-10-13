using Condux.Core.CveScanning;
using Condux.Core.FixEngine;

namespace Condux.Core.CveFix;

// The CVE bump verification policy (ADR-0051) and the types its verdict is made of. The stored form of
// a verdict, and the reasons it can carry, are in CveFixEvidence.cs.

/// <summary>How one sighting of a bumped package was read against the advisory (ADR-0051).</summary>
public enum SightingClass
{
    /// <summary>Confirmed inside one of the advisory's ranges for the package.</summary>
    Vulnerable = 0,

    /// <summary>Confirmed at or above the bump's target, and confirmed outside every range.</summary>
    Fixed = 1,

    /// <summary>Neither confirmed, which is not evidence either way.</summary>
    Undecided = 2,
}

/// <summary>One vulnerable range an advisory gives one package, as the advisory names them.</summary>
public sealed record AdvisoryRange(string Ecosystem, string Package, string VulnerableRange);

/// <summary>A sighting of the bumped package and how it was classed.</summary>
public sealed record ClassedSighting(ObservedModule Sighting, SightingClass Class);

/// <summary>The status a watching bump concludes to, the sightings it rested on, and, for Cannot verify,
/// why.</summary>
public sealed record CveFixVerdict(
    VerifyStatus Status, IReadOnlyList<ClassedSighting> Sightings, CannotVerifyReason? Reason = null);

/// <summary>
/// The CVE bump verification policy (ADR-0051), pure and clock free like <see cref="FixVerification"/>:
/// a merged bump holds once a fixed version is reported running and nothing vulnerable or undecided
/// has been reported for <see cref="QuietWindow"/>, is still exposed if a vulnerable version is still
/// reported at <see cref="Deadline"/>, and otherwise cannot be verified. "Reported" is the inventory's
/// last_seen, which is the client's own time, stamped about once a day.
/// </summary>
public static class CveFixVerification
{
    public static readonly TimeSpan QuietWindow = TimeSpan.FromDays(3);

    public static readonly TimeSpan Deadline = TimeSpan.FromDays(14);

    /// <summary>
    /// The verdict for one merged bump. <paramref name="ranges"/> is every vulnerable range the
    /// advisory gives the package; <paramref name="observed"/> may hold other packages and ecosystems,
    /// which are dropped here so the rule for which sightings count lives in one place.
    /// <paramref name="complete"/> says whether <paramref name="observed"/> holds every sighting since the
    /// merge: a read that stopped short may have left out a vulnerable one, so it can never hold.
    /// </summary>
    public static CveFixVerdict Evaluate(
        string ecosystem,
        string package,
        string target,
        IReadOnlyList<string> ranges,
        DateTimeOffset mergedAt,
        DateTimeOffset now,
        IReadOnlyList<ObservedModule> observed,
        bool complete)
    {
        var sightings = observed
            .Where(module => PackageEcosystems.Canonical(module.Ecosystem) == PackageEcosystems.Canonical(ecosystem)
                && PackageNames.AreSame(ecosystem, package, module.Package))
            .Select(module => new ClassedSighting(module, ToClass(ecosystem, target, ranges, module.Version)))
            .ToList();

        // Never decidable: concluded at once, since waiting cannot change the answer.
        if (!VulnerableRanges.CanDecide(ecosystem))
        {
            return new CveFixVerdict(VerifyStatus.CannotVerify, sightings, CannotVerifyReason.NoScheme);
        }
        if (ranges.Count == 0)
        {
            return new CveFixVerdict(VerifyStatus.CannotVerify, sightings, CannotVerifyReason.NoRanges);
        }

        var fixedSinceMerge = sightings.Any(s => s.Class == SightingClass.Fixed && s.Sighting.LastSeen > mergedAt);
        var otherwiseRecent = sightings.Any(s => s.Class != SightingClass.Fixed && IsInWindow(s, now));
        if (complete && now - mergedAt >= QuietWindow && fixedSinceMerge && !otherwiseRecent)
        {
            return new CveFixVerdict(VerifyStatus.Held, sightings);
        }
        if (now - mergedAt < Deadline)
        {
            return new CveFixVerdict(VerifyStatus.Watching, sightings);
        }

        if (sightings.Any(s => s.Class == SightingClass.Vulnerable && IsInWindow(s, now)))
        {
            return new CveFixVerdict(VerifyStatus.DidNotHold, sightings);
        }

        // Held needs a complete read, a fixed sighting since the merge and nothing else in the window, so
        // at the deadline the first of those missing is why it did not hold.
        var reason = !complete ? CannotVerifyReason.Incomplete
            : sightings.Count == 0 ? CannotVerifyReason.NothingReported
            : !fixedSinceMerge ? CannotVerifyReason.FixedNotReported
            : CannotVerifyReason.UndecidedReported;
        return new CveFixVerdict(VerifyStatus.CannotVerify, sightings, reason);
    }

    /// <summary>The evidence a verdict is stored with; <paramref name="complete"/> as the read reported.</summary>
    public static CveFixEvidence ToEvidence(
        CveFixVerdict verdict, string target, IReadOnlyList<string> ranges, DateTimeOffset mergedAt,
        DateTimeOffset now, bool complete) =>
        new(now, now - QuietWindow, mergedAt, target, ranges, complete, verdict.Reason,
        [
            .. verdict.Sightings.Select(s => new CveFixEvidenceSighting(
                s.Sighting.Version, s.Sighting.Environment, s.Sighting.Release, s.Sighting.LastSeen, s.Class,
                IsInWindow(s, now))),
        ]);

    /// <summary>
    /// The ranges a bump is judged against: its own <paramref name="fromRange"/>, then every other range
    /// the advisory gives the same package, which an advisory may spell differently ("django" and
    /// "Django"), so the package is matched by its registry's rule. Duplicates and blanks are dropped.
    /// </summary>
    public static IReadOnlyList<string> ToVulnerableRanges(
        string ecosystem, string package, string fromRange, IEnumerable<AdvisoryRange> advisory) =>
        [
            .. new[] { fromRange }
                .Concat(advisory
                    .Where(entry => PackageEcosystems.Canonical(entry.Ecosystem) == PackageEcosystems.Canonical(ecosystem)
                        && PackageNames.AreSame(ecosystem, package, entry.Package))
                    .Select(entry => entry.VulnerableRange))
                .Where(range => !string.IsNullOrWhiteSpace(range))
                .Distinct(StringComparer.Ordinal),
        ];

    // Stamped inside the quiet window that ends at now: the one rule for what counts as still reported,
    // used by the verdict and recorded on the evidence.
    private static bool IsInWindow(ClassedSighting sighting, DateTimeOffset now) =>
        sighting.Sighting.LastSeen > now - QuietWindow;

    private static SightingClass ToClass(string ecosystem, string target, IReadOnlyList<string> ranges, string version)
    {
        if (ranges.Any(range => VulnerableRanges.IsConfirmedInRange(ecosystem, range, version)))
        {
            return SightingClass.Vulnerable;
        }
        return ranges.Count > 0
            && VulnerableRanges.IsConfirmedAtOrAbove(ecosystem, target, version)
            && ranges.All(range => VulnerableRanges.IsConfirmedOutsideRange(ecosystem, range, version))
            ? SightingClass.Fixed
            : SightingClass.Undecided;
    }
}
