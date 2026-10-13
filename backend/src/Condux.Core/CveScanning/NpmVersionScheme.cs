namespace Condux.Core.CveScanning;

/// <summary>
/// How an npm advisory range reads: SemVer precedence, with npm's own rules where a range's bound or a
/// running version could be read more than one way. Checked against npm's real advisory ranges in
/// VulnerableRangesTests.
/// </summary>
internal sealed class NpmVersionScheme : IVersionScheme<SemanticVersion>
{
    public static readonly NpmVersionScheme Instance = new();

    private NpmVersionScheme()
    {
    }

    /// <summary>
    /// <b>A prerelease running version is undecided.</b> Precedence orders it, but npm's own range rule
    /// leaves prereleases out of a range unless a bound is a prerelease of the same version, and
    /// whether an advisory's author meant that rule is not knowable from the string. Claiming exposure
    /// there would over claim, which is the one direction this must not err in.
    /// </summary>
    public SemanticVersion? ParseRunning(string version) =>
        SemanticVersion.Parse(version) is { IsPrerelease: false } running ? running : null;

    /// <summary>
    /// A bound, which may be partial ("0", "1.2"), and is then padded with zeros. For a release version
    /// padding never admits more than npm does: under &gt;= and &lt; the two readings agree, and under
    /// &lt;= and = padding admits less ("&lt;= 1.2" pads to 1.2.0, where npm admits every 1.2.x), which
    /// can only miss a confirmation. Under &gt; it would admit more (npm reads "&gt; 0" as "&gt;=
    /// 1.0.0", padding as "&gt; 0.0.0"), so a partial bound there does not parse.
    ///
    /// <para>npm reads a partial "&lt;= N" as "&lt; N+1", and refuses the whole range when N+1 passes
    /// JavaScript's largest safe integer. A component of at most 15 digits cannot get there, so a
    /// partial bound with a longer one does not parse either.</para>
    ///
    /// <para>An <paramref name="exact"/> reading keeps a partial bound only where padding agrees with
    /// npm, under &gt;= and &lt;, and refuses it under &lt;= and =, where padding admits less than npm
    /// and so would put inside versions outside.</para>
    /// </summary>
    public SemanticVersion? ParseBound(string op, string text, bool exact)
    {
        var parts = text.Split('.');
        var partial = parts.Length < 3 && parts.All(p => p.Length is > 0 and <= 15 && p.All(char.IsAsciiDigit));
        if (!partial)
        {
            return SemanticVersion.Parse(text);
        }
        return op is not ">" && (!exact || op is ">=" or "<")
            ? SemanticVersion.Parse(string.Join('.', parts.Concat(Enumerable.Repeat("0", 3 - parts.Length))))
            : null;
    }

    public bool? Admits(string op, SemanticVersion bound, SemanticVersion running) =>
        VulnerableRanges.AdmitsByOrder(op, running.CompareTo(bound));
}
