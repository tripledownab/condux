namespace Condux.Core.CveScanning;

/// <summary>
/// A strict Semantic Versioning 2.0.0 version and its precedence, which is the ordering npm compares
/// versions by. Parsing and ordering live here; deciding what a range admits is
/// <see cref="VulnerableRanges"/>.
///
/// <para>Hand parsed rather than matched with the spec's regular expression, because a version reaches
/// this from an event, and whoever holds a DSN chooses it. A loop over the characters has no
/// backtracking to exploit, and <see cref="MaxLength"/> bounds the rest.</para>
///
/// <para>Strict on purpose. A loose form ("v1.2.3", "1.2", leading zeros) parses to null, and a caller
/// treats null as undecided rather than guessing what the sender meant.</para>
///
/// <para>A class and not a record, since a record's generated equality would compare the prerelease
/// list by reference and disagree with <see cref="CompareTo"/>.</para>
/// </summary>
public sealed class SemanticVersion : IComparable<SemanticVersion>
{
    private SemanticVersion(string major, string minor, string patch, string[] prerelease) =>
        (Major, Minor, Patch, Prerelease) = (major, minor, patch, prerelease);

    private string Major { get; }

    private string Minor { get; }

    private string Patch { get; }

    private IReadOnlyList<string> Prerelease { get; }

    /// <summary>Longer than any real version, short enough that comparing one costs nothing.</summary>
    public const int MaxLength = 256;

    /// <summary>Whether this is a prerelease, which sorts below the release it precedes.</summary>
    public bool IsPrerelease => Prerelease.Count > 0;

    /// <summary>
    /// The version, or null when the text is not a full SemVer 2.0.0 version. Build metadata is
    /// accepted and dropped, because the spec says it does not take part in precedence (clause 10).
    /// </summary>
    public static SemanticVersion? Parse(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > MaxLength)
        {
            return null;
        }

        var plus = text.IndexOf('+');
        if (plus >= 0 && !AreIdentifiers(text[(plus + 1)..], numericRule: false))
        {
            return null;
        }
        var core = plus >= 0 ? text[..plus] : text;

        var dash = core.IndexOf('-');
        var prerelease = dash >= 0 ? core[(dash + 1)..] : null;
        if (prerelease is not null && !AreIdentifiers(prerelease, numericRule: true))
        {
            return null;
        }

        var parts = (dash >= 0 ? core[..dash] : core).Split('.');
        if (parts.Length != 3 || !parts.All(IsNumber) || !parts.All(FitsNpm))
        {
            return null;
        }
        return new SemanticVersion(parts[0], parts[1], parts[2], prerelease?.Split('.') ?? []);
    }

    /// <summary>Precedence per the spec (clause 11). Build metadata was dropped at parse.</summary>
    public int CompareTo(SemanticVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var core = CompareNumbers(Major, other.Major);
        core = core != 0 ? core : CompareNumbers(Minor, other.Minor);
        core = core != 0 ? core : CompareNumbers(Patch, other.Patch);
        if (core != 0)
        {
            return core;
        }

        // A release sorts above every prerelease of the same version.
        if (IsPrerelease != other.IsPrerelease)
        {
            return IsPrerelease ? -1 : 1;
        }

        for (var i = 0; i < Math.Min(Prerelease.Count, other.Prerelease.Count); i++)
        {
            var order = CompareIdentifiers(Prerelease[i], other.Prerelease[i]);
            if (order != 0)
            {
                return order;
            }
        }
        return Prerelease.Count.CompareTo(other.Prerelease.Count);
    }

    // Numeric identifiers compare as numbers and sort below alphanumeric ones, which compare in ASCII
    // order.
    private static int CompareIdentifiers(string left, string right)
    {
        bool leftNumeric = left.All(char.IsAsciiDigit), rightNumeric = right.All(char.IsAsciiDigit);
        return (leftNumeric, rightNumeric) switch
        {
            (true, true) => CompareNumbers(left, right),
            (true, false) => -1,
            (false, true) => 1,
            _ => string.CompareOrdinal(left, right),
        };
    }

    // Compared as digit strings rather than parsed, since a numeric prerelease identifier has no length
    // limit. That holds only without leading zeros, which IsNumber refuses.
    private static int CompareNumbers(string left, string right) => DigitStrings.Compare(left, right);

    // npm refuses a major, minor or patch above JavaScript's largest safe integer, so a version past
    // it is one npm would never install and never compare.
    private static bool FitsNpm(string part) =>
        part.Length < MaxSafeInteger.Length
        || (part.Length == MaxSafeInteger.Length && string.CompareOrdinal(part, MaxSafeInteger) <= 0);

    private const string MaxSafeInteger = "9007199254740991";

    private static bool IsNumber(string part) =>
        part.Length > 0 && part.All(char.IsAsciiDigit) && (part.Length == 1 || part[0] != '0');

    // Dot separated, each non empty and drawn from [0-9A-Za-z-]. A prerelease identifier that is all
    // digits must also carry no leading zero (clause 9); build identifiers need not (clause 10).
    private static bool AreIdentifiers(string text, bool numericRule) =>
        text.Split('.').All(identifier =>
            identifier.Length > 0
            && identifier.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
            && (!numericRule || !identifier.All(char.IsAsciiDigit) || IsNumber(identifier)));
}
