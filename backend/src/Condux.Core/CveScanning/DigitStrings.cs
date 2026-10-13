namespace Condux.Core.CveScanning;

/// <summary>
/// Unsigned numbers kept as digit strings, for version schemes whose numbers have no size limit. A
/// version component can be longer than any integer type, and parsing one would either throw or wrap,
/// so the comparison works on the text.
/// </summary>
internal static class DigitStrings
{
    /// <summary>The order of two digit strings, which must carry no leading zeros.</summary>
    public static int Compare(string left, string right) =>
        left.Length != right.Length ? left.Length.CompareTo(right.Length) : string.CompareOrdinal(left, right);

    /// <summary>The same number without its leading zeros, keeping a lone "0".</summary>
    public static string TrimLeadingZeros(string digits)
    {
        var trimmed = digits.TrimStart('0');
        return trimmed.Length == 0 ? "0" : trimmed;
    }
}
