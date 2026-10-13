namespace Condux.Core.CveScanning;

/// <summary>
/// How a PyPI advisory range reads: <see cref="Pep440Version"/> order, with the PyPA version specifiers
/// specification's rules for the comparisons where plain order says more than the specification does.
/// Checked against real GitHub pip advisory ranges, decided by Python's own packaging library, in
/// PypiVulnerableRangesTests.
/// </summary>
internal sealed class PypiVersionScheme : IVersionScheme<Pep440Version>
{
    public static readonly PypiVersionScheme Instance = new();

    private PypiVersionScheme()
    {
    }

    /// <summary>
    /// <b>A pre-release or development running version is undecided</b>, as for npm: whether a range
    /// admits one depends on who reads it (the specification leaves pre-releases out by default and
    /// has an installer accept one already installed), and an advisory's string does not say which
    /// reading its author meant. A local label ("2.1.0+cu118") is kept, and the
    /// build is matched as the release it was built from, which is what the specification does
    /// everywhere except under "&gt;" (see <see cref="Admits"/>).
    /// </summary>
    public Pep440Version? ParseRunning(string version) =>
        Pep440Version.Parse(version) is { IsPrerelease: false } running ? running : null;

    /// <summary>
    /// A bound, read exactly either way. A partial one needs no padding, since the order already reads
    /// 1.2 as 1.2.0. A bound with a local label does not parse, because the specification permits one
    /// only under ==, != and ===, none of which this grammar reads as a local match, and the order
    /// here ignores it, so such a range would be read as something it does not say.
    /// </summary>
    public Pep440Version? ParseBound(string op, string text, bool exact) =>
        Pep440Version.Parse(text) is { HasLocal: false } bound ? bound : null;

    /// <summary>
    /// Order, except where the specification's exclusive "&gt; V" says less than order does: it
    /// admits neither a post-release of V's release unless V is itself one (plain order would admit
    /// 1.0.post1 under "&gt; 1.0"), nor any local build of V's release (plain order would admit
    /// 1.0+cu118 under "&gt; 1.0rc1"). Both are what packaging does. The matching rule for a
    /// pre-release under "&lt; V" never applies, since a pre-release running version is undecided
    /// before it gets here.
    /// </summary>
    public bool? Admits(string op, Pep440Version bound, Pep440Version running) =>
        VulnerableRanges.AdmitsByOrder(op, running.CompareTo(bound))
        && !(op == ">"
            && running.HasSameBase(bound)
            && ((running.IsPostRelease && !bound.IsPostRelease) || running.HasLocal));
}
