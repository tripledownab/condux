using System.Text.RegularExpressions;

namespace Condux.Core.CveScanning;

/// <summary>
/// A Maven version, read only as far as every Maven ordering agrees: its leading run of numbers, and
/// whether anything follows them. What a range admits is <see cref="MavenVersionScheme"/>.
///
/// <para><b>Why so little.</b> There is no single Maven order to follow. Maven 3 and Maven 4 order
/// qualifiers differently (measured: Maven 3.9.16 puts "15.10.redhat-00001" below "15.10-rc-1" and
/// Maven 4.0.0-rc-7 above it), and Dependabot, whose advisories these are, parses versions with a
/// third implementation of its own. They agree on the numbers: where two versions' leading numbers
/// differ, every one orders them by those numbers. So that is the whole order here, and two versions
/// whose numbers are equal are undecided unless neither carries anything after them, or their texts
/// are the same but for case. Two qualifiers are read, each only on its own after the numbers, where all three
/// agree on them: "final", "ga" or "release" is the release itself, and a pre-release qualifier
/// ("-alpha1", "-rc-1", "-SNAPSHOT", "-M2") sorts below the release with the same numbers.</para>
///
/// <para><b>A pre-release is decided like any version.</b> Its numbers are read the same way by every
/// order, so "2.1-SNAPSHOT" is inside "&lt; 2.5" for all of them, and with equal numbers it is
/// undecided unless the other side is the bare release. Unlike npm and PyPI, a Maven range has no rule
/// of its own that leaves pre-releases out.</para>
/// </summary>
public sealed class MavenVersion
{
    // A pre-release qualifier on its own: one of Maven's pre-release words, optionally numbered, or the
    // short forms a, b and m directly before a number. Maven 3 reads "b-1" as an unknown qualifier
    // sorting after the release and Maven 4 as a beta, so the short forms need the digit attached.
    private static readonly Regex PrereleaseQualifier = new(
        "^[.-]?((alpha|beta|milestone|rc|cr|snapshot)([.-]?[0-9]+)?|[abm][0-9]+)$", RegexOptions.CultureInvariant);

    private MavenVersion(string text, string[] numbers, string rest) => (Text, Numbers, Rest) = (text, numbers, rest);

    private string Text { get; }

    private string[] Numbers { get; }

    // What follows the leading numbers, lowercased, which this order never reads past.
    private string Rest { get; }

    /// <summary>
    /// The version, or null when the text is empty, longer than <see cref="SemanticVersion.MaxLength"/>,
    /// does not start with a digit, or holds anything but ASCII letters, digits, '.', '-' and '_'.
    /// </summary>
    public static MavenVersion? Parse(string? text)
    {
        if (string.IsNullOrEmpty(text)
            || text.Length > SemanticVersion.MaxLength
            || !char.IsAsciiDigit(text[0])
            || !text.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'))
        {
            return null;
        }

        var numbers = new List<string>();
        var position = 0;
        while (position < text.Length && char.IsAsciiDigit(text[position]))
        {
            var start = position;
            while (position < text.Length && char.IsAsciiDigit(text[position]))
            {
                position++;
            }
            numbers.Add(DigitStrings.TrimLeadingZeros(text[start..position]));
            if (position + 1 < text.Length && text[position] == '.' && char.IsAsciiDigit(text[position + 1]))
            {
                position++;
            }
            else
            {
                break;
            }
        }
        var rest = text[position..].ToLowerInvariant();
        return new MavenVersion(text.ToLowerInvariant(), [.. numbers], IsReleaseWord(rest) ? "" : rest);
    }

    /// <summary>
    /// The order of two versions where every Maven ordering agrees on it, or null where they may not.
    /// The first leading number that differs decides it, and a version that has run out of numbers
    /// counts as zero there only if nothing follows its numbers: in "1.0-1" against "1.0.0.5" the
    /// "-1" would be weighed against a number, which is qualifier ordering this does not read. Equal numbers with nothing after either are equal, and so are
    /// texts identical but for case, and a bare release is above its own pre-release. Anything else
    /// is null.
    /// </summary>
    public int? CompareTo(MavenVersion other)
    {
        for (var i = 0; i < Math.Max(Numbers.Length, other.Numbers.Length); i++)
        {
            if ((i >= Numbers.Length && Rest.Length > 0) || (i >= other.Numbers.Length && other.Rest.Length > 0))
            {
                return null;
            }
            var order = DigitStrings.Compare(
                i < Numbers.Length ? Numbers[i] : "0", i < other.Numbers.Length ? other.Numbers[i] : "0");
            if (order != 0)
            {
                return order;
            }
        }
        return (Rest, other.Rest) switch
        {
            ("", "") => 0,
            _ when Text == other.Text => 0,
            ("", var theirs) when PrereleaseQualifier.IsMatch(theirs) => 1,
            (var ours, "") when PrereleaseQualifier.IsMatch(ours) => -1,
            _ => null,
        };
    }

    // ".Final", "-ga" and the like name the release itself in Maven 3, Maven 4 and Dependabot alike, as
    // long as nothing else follows ("1.0.final.1" is not 1.0 in Maven).
    private static bool IsReleaseWord(string rest) =>
        rest.Length > 1 && rest[0] is '.' or '-' && rest[1..] is "final" or "ga" or "release";
}
