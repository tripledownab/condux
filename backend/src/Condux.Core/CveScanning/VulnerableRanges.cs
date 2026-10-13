namespace Condux.Core.CveScanning;

/// <summary>
/// Whether a version observed running sits inside an advisory's vulnerable range. The answer is
/// asymmetric by design: true is a confirmed match, and false means only that nothing was confirmed.
/// A false is never evidence that the version is unaffected, so no caller may present it as one.
///
/// <para><b>One ecosystem at a time.</b> Ordering versions is ecosystem specific, and a comparator that
/// is wrong for an ecosystem answers as confidently as a right one. So an ecosystem is added here only
/// with a test against that ecosystem's real advisory ranges, and every other ecosystem answers false.
/// npm (<see cref="NpmVersionScheme"/>), PyPI (<see cref="PypiVersionScheme"/>) and Maven
/// (<see cref="MavenVersionScheme"/>) are the ones today.</para>
///
/// <para>The grammar is a list of comparators that must all hold, each an operator (&lt;, &lt;=, &gt;,
/// &gt;=, =) and a version, separated by commas or spaces. That covers GitHub's form
/// ("&gt;= 4.0.0, &lt; 4.17.21") and the one <see cref="OsvScanOutput"/> builds ("&gt;=4.0.0
/// &lt;4.17.21"). Anything else, an empty range included, answers false.</para>
/// </summary>
public static class VulnerableRanges
{
    private static readonly string[] Operators = [">=", "<=", ">", "<", "="];

    // What separates one comparator from the next. A version handed in as a bound must hold neither.
    private static readonly char[] Separators = [',', ' '];

    /// <summary>
    /// True when the ecosystem has a scheme, <paramref name="version"/> is a running version that
    /// scheme can decide, <paramref name="range"/> parses, and every comparator in the range admits the
    /// version. False otherwise, including for every reason the question cannot be decided.
    /// </summary>
    public static bool IsConfirmedInRange(string ecosystem, string range, string version) =>
        Ask(ecosystem, range, version, inside: true);

    /// <summary>
    /// True when <paramref name="version"/> is confirmed OUTSIDE <paramref name="range"/>: the range
    /// parses, the version is one the scheme decides, and some comparator is decided against it. Not
    /// the negation of <see cref="IsConfirmedInRange"/>, since both are false when nothing can be
    /// decided.
    ///
    /// <para>Each bound must be read exactly, which is stricter than confirming inside: a partial npm
    /// bound is padded for that ("&lt;= 1.2" as 1.2.0, which only ever admits less than npm does), and
    /// read that way it would put 1.2.5 outside a range npm puts it inside. So here a partial npm bound
    /// under &lt;= or = does not parse, and its range is undecided.</para>
    /// </summary>
    public static bool IsConfirmedOutsideRange(string ecosystem, string range, string version) =>
        Ask(ecosystem, range, version, inside: false);

    /// <summary>
    /// True when <paramref name="version"/> is confirmed at or above <paramref name="minimum"/> (a
    /// bump's target, or an advisory's first patched version), by the same ecosystem rules as
    /// <see cref="IsConfirmedInRange"/>: it asks that question of the range "&gt;= minimum". False
    /// does not mean below: an ecosystem with no scheme, a pre-release on npm or PyPI, and a Maven
    /// qualifier this does not read all answer false too.
    ///
    /// <para>The minimum must be a single version. One holding a comma or a space would add comparators
    /// of its own to the range, so it answers false; an operator in it already fails to parse as the
    /// bound.</para>
    /// </summary>
    public static bool IsConfirmedAtOrAbove(string ecosystem, string minimum, string version) =>
        minimum is not null
        && minimum.IndexOfAny(Separators) < 0
        && IsConfirmedInRange(ecosystem, ">= " + minimum, version);

    /// <summary>
    /// Whether the ecosystem has a scheme here at all, so that a caller can tell "this can never be
    /// decided" from "this was not decided". Must name exactly the ecosystems <see cref="Ask"/>
    /// dispatches, pinned by VulnerableRangesTests.
    /// </summary>
    public static bool CanDecide(string ecosystem) =>
        PackageEcosystems.Canonical(ecosystem) is "npm" or "PyPI" or "Maven";

    /// <summary>Whether a comparator admits a version that orders against its bound as given.</summary>
    internal static bool AdmitsByOrder(string op, int order) => op switch
    {
        ">=" => order >= 0,
        "<=" => order <= 0,
        ">" => order > 0,
        "<" => order < 0,
        _ => order == 0,
    };

    private static bool Ask(string ecosystem, string range, string version, bool inside)
    {
        // The range is typed non null, but it is a scanner's field and a scanner can send null.
        if (range is null || range.Length > SemanticVersion.MaxLength)
        {
            return false;
        }

        return PackageEcosystems.Canonical(ecosystem) switch
        {
            "npm" => Decide(NpmVersionScheme.Instance, range, version, inside),
            "PyPI" => Decide(PypiVersionScheme.Instance, range, version, inside),
            "Maven" => Decide(MavenVersionScheme.Instance, range, version, inside),
            _ => false,
        };
    }

    // Inside needs every comparator to admit the version; outside needs one decided against it, since
    // the comparators are all required.
    private static bool Decide<T>(IVersionScheme<T> scheme, string range, string version, bool inside)
        where T : class
    {
        var running = scheme.ParseRunning(version);
        if (running is null || ParseComparators(scheme, range, exact: !inside) is not { Count: > 0 } comparators)
        {
            return false;
        }

        var answers = comparators.Select(c => scheme.Admits(c.Operator, c.Bound, running));
        return inside ? answers.All(admitted => admitted == true) : answers.Any(admitted => admitted == false);
    }

    // Null when any token fails to parse, so a range that is partly understood is not understood.
    private static List<(string Operator, T Bound)>? ParseComparators<T>(
        IVersionScheme<T> scheme, string range, bool exact)
        where T : class
    {
        var tokens = range.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        var comparators = new List<(string, T)>();
        for (var i = 0; i < tokens.Length; i++)
        {
            var op = Operators.FirstOrDefault(o => tokens[i].StartsWith(o, StringComparison.Ordinal));
            if (op is null)
            {
                return null;
            }

            // GitHub puts a space between the operator and the version; the OSV form does not.
            var text = tokens[i].Length > op.Length ? tokens[i][op.Length..] : i + 1 < tokens.Length ? tokens[++i] : "";
            var bound = scheme.ParseBound(op, text, exact);
            if (bound is null)
            {
                return null;
            }
            comparators.Add((op, bound));
        }
        return comparators;
    }
}

/// <summary>
/// One ecosystem's reading of a vulnerable range: which running versions it can decide, what a bound
/// is, and whether a comparator admits a version. Each returns null when it cannot say.
/// </summary>
internal interface IVersionScheme<T>
    where T : class
{
    /// <summary>The running version, or null when this scheme cannot decide it.</summary>
    T? ParseRunning(string version);

    /// <summary>
    /// The bound of a comparator with this operator, or null when it cannot be read. When
    /// <paramref name="exact"/>, null also for a bound read approximately.
    /// </summary>
    T? ParseBound(string op, string text, bool exact);

    /// <summary>Whether the comparator admits the version, or null when this scheme cannot say.</summary>
    bool? Admits(string op, T bound, T running);
}
