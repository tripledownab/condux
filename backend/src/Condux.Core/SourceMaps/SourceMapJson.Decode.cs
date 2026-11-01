using System.Text.Json;
using System.Text.Unicode;

namespace Condux.Core.SourceMaps;

/// <summary>
/// The decode half of <see cref="SourceMapJson"/>, for the maps a caller uses: measuring a mappings string,
/// reading strings and indexes out of the ranges the scan noted, or, for the upload check, confirming those
/// strings are valid text without keeping them. The check and a read go through the same ranges, so a map
/// the check accepts is a map a read can use.
/// </summary>
internal static partial class SourceMapJson
{
    /// <summary>What a mappings string would decode to. Counted where it lies when it holds no escapes,
    /// which builds nothing; one with escapes is read as the decoder will read it. Throws
    /// <see cref="JsonException"/> when it is not valid text.</summary>
    public static (long Lines, long Segments) MeasureMappings(byte[] json, JsonRange mappings)
    {
        var reader = At(json, mappings);
        if (!reader.ValueIsEscaped)
        {
            CheckUtf8(reader.ValueSpan);
            return SourceMapMappings.Measure(reader.ValueSpan);
        }

        return SourceMapMappings.Measure(ToText(ref reader));
    }

    /// <summary>A mappings string, as the decoder reads it.</summary>
    public static string ToMappings(byte[] json, JsonRange mappings)
    {
        var reader = At(json, mappings);
        return ToText(ref reader);
    }

    /// <summary>The items of a string array, with null for an item that is not a string; empty when the
    /// field is missing or not an array.</summary>
    public static string?[] ToStrings(byte[] json, JsonRange? field) => [.. ReadStrings(json, field, keep: true)!];

    /// <summary>Throws <see cref="JsonException"/> unless every string a decode of the map would read out of
    /// its sources, contents and names is valid text. Keeps none of them.</summary>
    public static void CheckStrings(byte[] json, SourceMapFields map)
    {
        ReadStrings(json, map.Sources, keep: false);
        ReadStrings(json, map.SourcesContent, keep: false);
        ReadStrings(json, map.Names, keep: false);
    }

    /// <summary>ECMA-426 GetOptionalListOfArrayIndexes: a missing or non-array field is an empty list, and
    /// only the integral, non-negative numbers in it count.</summary>
    public static HashSet<int> ToIndexes(byte[] json, JsonRange? field)
    {
        var indexes = new HashSet<int>();
        if (field is not { } range)
        {
            return indexes;
        }

        var reader = At(json, range);
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            return indexes;
        }
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetDouble(out var value)
                && value >= 0 && value <= int.MaxValue && value == Math.Floor(value))
            {
                indexes.Add((int)value);
            }
            reader.Skip();
        }

        return indexes;
    }

    // With keep false nothing is kept: a string with no escapes is checked where it lies, and one with
    // escapes is read and dropped, since unescaping is the only way to find what it spells.
    private static List<string?>? ReadStrings(byte[] json, JsonRange? field, bool keep)
    {
        var items = keep ? new List<string?>() : null;
        if (field is not { } range)
        {
            return items;
        }

        var reader = At(json, range);
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            return items;
        }
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.String)
            {
                items?.Add(null);
            }
            else if (keep)
            {
                items!.Add(ToText(ref reader));
            }
            else if (reader.ValueIsEscaped)
            {
                ToText(ref reader);
            }
            else
            {
                CheckUtf8(reader.ValueSpan);
            }
            reader.Skip();
        }

        return items;
    }

    // A reader on one value the scan noted, positioned on its first token.
    private static Utf8JsonReader At(byte[] json, JsonRange range)
    {
        var reader = new Utf8JsonReader(json.AsSpan(range.Start, range.Length));
        reader.Read();
        return reader;
    }

    // GetString throws InvalidOperationException for a string that is not text (invalid UTF-8, or an
    // escaped half of a surrogate pair). Such a document is malformed, so it is reported as one, which is
    // what every caller already answers.
    private static string ToText(ref Utf8JsonReader reader)
    {
        try
        {
            return reader.GetString()!;
        }
        catch (InvalidOperationException e)
        {
            throw new JsonException("a string in the source map is not valid text", e);
        }
    }

    private static void CheckUtf8(ReadOnlySpan<byte> utf8)
    {
        if (!Utf8.IsValid(utf8))
        {
            throw new JsonException("a string in the source map is not valid UTF-8");
        }
    }
}
