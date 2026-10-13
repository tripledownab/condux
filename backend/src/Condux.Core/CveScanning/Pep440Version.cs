namespace Condux.Core.CveScanning;

/// <summary>
/// A Python package version as the PyPA version specifiers specification defines it (the successor to
/// PEP 440), and its ordering. Ordering lives here, parsing in Pep440Version.Parsing.cs, and what a
/// range admits in <see cref="PypiVersionScheme"/>.
///
/// <para>The parser accepts every spelling the specification's normalisation section accepts, case
/// insensitively and with surrounding whitespace: a leading "v", the alternate pre-release words
/// ("alpha", "beta", "c", "pre", "preview"), the post-release words ("rev", "r") and the implicit "1.0-1"
/// form, one optional separator before each suffix, and an omitted suffix number. Anything else parses
/// to null, which a caller reads as undecided. Case is folded by lowercasing the text, which folds
/// fewer characters than packaging's case insensitive match: packaging also reads "ſ", "ı" and "İ"
/// as ASCII letters, and those versions are refused here, which is the undecided side.</para>
///
/// <para>Hand parsed for the reason <see cref="SemanticVersion"/> is: a running version arrives in an
/// event, and whoever holds a DSN chooses it. Numbers are compared as digit strings, since the
/// specification sets no limit on them, and <see cref="SemanticVersion.MaxLength"/> bounds the work.</para>
/// </summary>
public sealed partial class Pep440Version : IComparable<Pep440Version>
{
    private Pep440Version(
        string epoch, string[] release, (int Kind, string Number)? pre, string? post, string? dev, bool hasLocal)
    {
        Epoch = epoch;
        // 1.0 and 1.0.0 are one release. The order pads the shorter one anyway; HasSameBase needs this.
        var length = release.Length;
        while (length > 1 && release[length - 1] == "0")
        {
            length--;
        }
        Release = release[..length];
        Pre = pre;
        Post = post;
        Dev = dev;
        HasLocal = hasLocal;
    }

    private string Epoch { get; }

    private string[] Release { get; }

    // Kind orders a, b, rc as 0, 1, 2.
    private (int Kind, string Number)? Pre { get; }

    private string? Post { get; }

    private string? Dev { get; }

    /// <summary>Whether the version carried a local label ("+cu118"), which ordering ignores.</summary>
    public bool HasLocal { get; }

    /// <summary>A pre-release or a development release, as the specification counts them.</summary>
    public bool IsPrerelease => Pre is not null || Dev is not null;

    /// <summary>A post-release.</summary>
    public bool IsPostRelease => Post is not null;

    /// <summary>Whether two versions share an epoch and a release, whatever their suffixes.</summary>
    public bool HasSameBase(Pep440Version other) =>
        Epoch == other.Epoch && Release.SequenceEqual(other.Release);

    /// <summary>
    /// The specification's order, which is packaging's: epoch, then release, then a development release
    /// of nothing below every pre-release, pre-releases a &lt; b &lt; rc, the release, its post-releases,
    /// and within each a development release below the one it leads to. A local label is ignored; the
    /// one comparison that looks at it is <see cref="PypiVersionScheme"/>'s "&gt;".
    /// </summary>
    public int CompareTo(Pep440Version? other)
    {
        if (other is null)
        {
            return 1;
        }

        var order = DigitStrings.Compare(Epoch, other.Epoch);
        for (var i = 0; order == 0 && i < Math.Max(Release.Length, other.Release.Length); i++)
        {
            order = DigitStrings.Compare(
                i < Release.Length ? Release[i] : "0", i < other.Release.Length ? other.Release[i] : "0");
        }
        order = order != 0 ? order : ToPreRank().CompareTo(other.ToPreRank());
        order = order != 0 || Pre is null || other.Pre is null
            ? order
            : DigitStrings.Compare(Pre.Value.Number, other.Pre.Value.Number);
        order = order != 0 ? order : Compare(Post, other.Post, absentSortsHigh: false);
        return order != 0 ? order : Compare(Dev, other.Dev, absentSortsHigh: true);
    }

    // A development release of the release itself sorts below its pre-releases, the release without a
    // pre-release above them.
    private int ToPreRank() => Pre?.Kind ?? (Post is null && Dev is not null ? -1 : 3);

    private static int Compare(string? left, string? right, bool absentSortsHigh) => (left, right) switch
    {
        (null, null) => 0,
        (null, _) => absentSortsHigh ? 1 : -1,
        (_, null) => absentSortsHigh ? -1 : 1,
        _ => DigitStrings.Compare(left, right),
    };
}
