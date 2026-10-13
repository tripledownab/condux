using System.Text.RegularExpressions;

namespace Condux.Core.CveScanning;

/// <summary>
/// Whether two package names name the same package, by the rule of the registry the ecosystem
/// belongs to. The one statement of that rule, for matching and confirming a finding's exposure and
/// for verifying a CVE bump alike.
/// </summary>
public static class PackageNames
{
    // PEP 503's name normalisation: runs of '-', '_' and '.' are one separator.
    private static readonly Regex PypiSeparators = new("[-_.]+", RegexOptions.CultureInvariant);

    /// <summary>
    /// Exact, except on PyPI, where a name is normalised by definition (the simple repository API,
    /// formerly PEP 503): case folded and each run of '-', '_' and '.' read as one '-'. So "Django" from
    /// an advisory and "django" from a runtime are one project, and so are "jupyter_server" and
    /// "jupyter-server", which one project's own releases report both ways. Elsewhere exact, which is
    /// right for npm and Maven, whose names are case sensitive and keep their separators: npm holds
    /// distinct packages that differ only in case ("Proto" and "proto") or in a separator ("socket.io"
    /// and "socket-io"). NuGet ignores case, but has no scheme that could confirm anything.
    /// </summary>
    public static bool AreSame(string ecosystem, string left, string right) =>
        IsPypi(ecosystem) ? ToPypiName(left) == ToPypiName(right) : string.Equals(left, right, StringComparison.Ordinal);

    /// <summary>
    /// A key two spellings of one package share, for matching a sighting leniently: PyPI's normalised
    /// name there, and the name with its ASCII letters lowercased elsewhere. It may join two packages that
    /// <see cref="AreSame"/> keeps apart, which is the point of a lenient match, and never the reverse.
    /// </summary>
    public static string ToMatchKey(string ecosystem, string name) =>
        IsPypi(ecosystem) ? ToPypiName(name) : ToAsciiLower(name);

    /// <summary>
    /// The coarsest key any ecosystem's <see cref="ToMatchKey"/> refines: ASCII letters lowercased,
    /// with each run of '-', '_' and '.' read as one '-'. A store filters on it to fetch every row any
    /// ecosystem might match, and the exact rule then picks among them. ASCII only on purpose: the
    /// inventory's query lowercases with ClickHouse's lower(), which leaves other letters alone, and a
    /// key that lowercased more would miss a stored name holding one.
    /// </summary>
    public static string ToFilterKey(string name) => ToPypiName(name);

    private static bool IsPypi(string ecosystem) => PackageEcosystems.Canonical(ecosystem) == "PyPI";

    private static string ToPypiName(string name) => ToAsciiLower(PypiSeparators.Replace(name, "-"));

    private static string ToAsciiLower(string text) =>
        string.Create(text.Length, text, (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                span[i] = char.IsAsciiLetterUpper(source[i]) ? (char)(source[i] + ('a' - 'A')) : source[i];
            }
        });
}
