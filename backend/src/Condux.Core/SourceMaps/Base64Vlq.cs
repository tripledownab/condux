namespace Condux.Core.SourceMaps;

/// <summary>
/// Base64 VLQ decoding for source-map "mappings" segments (Source Map v3 / ECMA-426). Digits are
/// little-endian (least significant first); each digit carries a continuation bit (bit 5) and 5 value
/// bits; the least significant bit of the decoded value is the sign. Implemented fresh from the spec.
/// </summary>
internal static class Base64Vlq
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    private static readonly int[] Lookup = BuildLookup();

    private static int[] BuildLookup()
    {
        var lookup = new int[128];
        Array.Fill(lookup, -1);
        for (var i = 0; i < Alphabet.Length; i++)
        {
            lookup[Alphabet[i]] = i;
        }

        return lookup;
    }

    /// <summary>
    /// Decode every VLQ value in a single segment string into <paramref name="values"/>, returning the
    /// count (a segment holds 1, 4, or 5 values). Returns -1 on an invalid character, a truncated value,
    /// or more values than <paramref name="values"/> can hold.
    /// </summary>
    public static int Decode(ReadOnlySpan<char> segment, Span<int> values)
    {
        var count = 0;
        var i = 0;
        while (i < segment.Length)
        {
            var result = 0;
            var shift = 0;
            bool continuation;
            do
            {
                if (i >= segment.Length)
                {
                    return -1; // truncated: a continuation digit with nothing after it
                }

                var c = segment[i++];
                var digit = c < 128 ? Lookup[c] : -1;
                if (digit < 0)
                {
                    return -1; // not a base64 digit
                }

                continuation = (digit & 32) != 0;
                result += (digit & 31) << shift;
                shift += 5;
            }
            while (continuation);

            var value = (result & 1) != 0 ? -(result >> 1) : result >> 1;
            if (count >= values.Length)
            {
                return -1; // more fields than a segment should carry
            }

            values[count++] = value;
        }

        return count;
    }
}
