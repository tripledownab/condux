namespace Condux.Core.CveScanning;

/// <summary>
/// A vulnerability's severity, normalized across sources. Dependabot reports low/moderate/high/critical,
/// OSV carries CVSS vectors and scores, and other forges use their own words; the surface and the bump
/// decision need one scale to sort and filter by.
/// </summary>
public enum CveSeverity
{
    Unknown = 0,
    Low = 1,
    Moderate = 2,
    High = 3,
    Critical = 4,
}

/// <summary>
/// One vulnerable dependency, in a shape no scanner's wire format leaks into. The CVE surface and the
/// dependency-bump run both consume this, so adding a scanner is an implementation rather than a change
/// to either.
/// </summary>
/// <param name="AdvisoryId">The advisory's canonical id (a GHSA id where one exists). The bump run keys on this.</param>
/// <param name="CveId">The CVE id when the advisory has one; many advisories do not.</param>
/// <param name="Package">The vulnerable package's name, as its ecosystem spells it.</param>
/// <param name="Ecosystem">The package ecosystem ("npm", "pip", "Go", ...), as OSV names it.</param>
/// <param name="VulnerableRange">The affected version range, verbatim from the source.</param>
/// <param name="FixedVersion">The first version carrying the fix, when the advisory names one.</param>
/// <param name="Url">Where a human reads the advisory.</param>
/// <param name="Source">Which scanner produced this, so a mixed result set stays explainable.</param>
public sealed record CveFinding(
    string AdvisoryId,
    string? CveId,
    CveSeverity Severity,
    string Summary,
    string Package,
    string Ecosystem,
    string VulnerableRange,
    string? FixedVersion,
    string Url,
    string Source)
{
    /// <summary>Whether the advisory names a version to bump to. Only a fixable finding can be bumped.</summary>
    public bool IsFixable => !string.IsNullOrWhiteSpace(FixedVersion);

    /// <summary>The repo-relative manifest the dependency is declared in, when the scanner knows it —
    /// what points a bump at a monorepo workspace member instead of the repo root. Null when unknown
    /// (the bump then falls back to root manifests).</summary>
    public string? ManifestPath { get; init; }
}

/// <summary>
/// Finds vulnerable dependencies in a repository. GitHub reads Dependabot's already-computed alerts;
/// every other forge runs a scanner over the repo's lockfiles. Both answer in <see cref="CveFinding"/>.
/// </summary>
public interface ICveScanner
{
    /// <summary>Which scanner this is, recorded on each finding.</summary>
    string Name { get; }

    /// <summary>
    /// Open vulnerabilities for a repository. A scanner that cannot run returns nothing rather than
    /// throwing: the CVE surface is advisory, and a missing permission or an unreachable service must not
    /// turn a page load into an error.
    /// </summary>
    Task<IReadOnlyList<CveFinding>> ScanAsync(
        string token, string repoFullName, CancellationToken cancellationToken = default);
}

/// <summary>Severity parsing and result normalization, kept pure so every scanner agrees on both.</summary>
public static class CveSeverities
{
    /// <summary>
    /// Map a source's severity word to the normalized scale. Unrecognized input is Unknown rather than a
    /// guess, because sorting a misread "moderate" above a real "critical" is worse than admitting we do
    /// not know.
    /// </summary>
    public static CveSeverity Parse(string? severity) => severity?.Trim().ToLowerInvariant() switch
    {
        "critical" => CveSeverity.Critical,
        "high" => CveSeverity.High,
        // Dependabot says "moderate", CVSS qualitative says "medium"; they are the same band.
        "moderate" or "medium" => CveSeverity.Moderate,
        "low" => CveSeverity.Low,
        _ => CveSeverity.Unknown,
    };

    /// <summary>
    /// The CVSS v3 qualitative band for a base score, for sources that report a number and no word.
    /// Thresholds are the ones CVSS defines, not ours.
    /// </summary>
    public static CveSeverity FromCvssScore(double score) => score switch
    {
        >= 9.0 => CveSeverity.Critical,
        >= 7.0 => CveSeverity.High,
        >= 4.0 => CveSeverity.Moderate,
        > 0.0 => CveSeverity.Low,
        _ => CveSeverity.Unknown,
    };

    /// <summary>
    /// Collapse duplicates and order worst-first. Two scanners can report the same advisory for the same
    /// package, and one scanner can report it once per lockfile it appears in; the surface should show it
    /// once. Keyed on package and advisory rather than advisory alone, since one advisory can legitimately
    /// affect several packages.
    /// </summary>
    public static IReadOnlyList<CveFinding> Normalize(IEnumerable<CveFinding> findings) =>
    [
        .. findings
            .GroupBy(f => (f.Package, f.AdvisoryId), StringTupleComparer.Ordinal)
            // Prefer the entry that names a fix: a finding you can act on beats an identical one you cannot.
            .Select(group => group.OrderByDescending(f => f.IsFixable).First())
            .OrderByDescending(f => f.Severity)
            .ThenBy(f => f.Package, StringComparer.Ordinal)
            .ThenBy(f => f.AdvisoryId, StringComparer.Ordinal),
    ];

    private sealed class StringTupleComparer : IEqualityComparer<(string, string)>
    {
        public static readonly StringTupleComparer Ordinal = new();

        public bool Equals((string, string) x, (string, string) y) =>
            string.Equals(x.Item1, y.Item1, StringComparison.Ordinal)
            && string.Equals(x.Item2, y.Item2, StringComparison.Ordinal);

        public int GetHashCode((string, string) obj) => HashCode.Combine(obj.Item1, obj.Item2);
    }
}
