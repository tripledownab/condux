using System.Globalization;
using System.Text.Json;

namespace Condux.Core.SourceMaps;

/// <summary>
/// The property names <see cref="SourceMapJson"/> reads, and how a name in a map, section or offset object
/// is matched to one of them.
/// </summary>
internal static partial class SourceMapJson
{
    private enum Field { Other, Mappings, Version, Sections, Sources, SourcesContent, Names, IgnoreList, XGoogleIgnoreList, Offset, Map, Line, Column }

    // The longest name below, "x_google_ignoreList".
    private const int LongestFieldName = 19;

    // An escaped name is unescaped here rather than by the reader, which throws for one that is not text (an
    // escaped half of a surrogate pair): one exception per name let a map of nothing but such names cost many
    // times its size. Every name below is ASCII letters and underscores, so an escape that is not "\u", or
    // spells more than a byte, ends the match without decoding the rest.
    private static Field ToField(ref Utf8JsonReader reader)
    {
        Span<byte> unescaped = stackalloc byte[LongestFieldName];
        scoped ReadOnlySpan<byte> name = reader.ValueSpan;
        if (reader.ValueIsEscaped)
        {
            if (!TryUnescapeName(name, unescaped, out var length))
            {
                return Field.Other;
            }

            name = unescaped[..length];
        }

        return name.SequenceEqual("mappings"u8) ? Field.Mappings
            : name.SequenceEqual("version"u8) ? Field.Version
            : name.SequenceEqual("sections"u8) ? Field.Sections
            : name.SequenceEqual("sources"u8) ? Field.Sources
            : name.SequenceEqual("sourcesContent"u8) ? Field.SourcesContent
            : name.SequenceEqual("names"u8) ? Field.Names
            : name.SequenceEqual("ignoreList"u8) ? Field.IgnoreList
            : name.SequenceEqual("x_google_ignoreList"u8) ? Field.XGoogleIgnoreList
            : name.SequenceEqual("offset"u8) ? Field.Offset
            : name.SequenceEqual("map"u8) ? Field.Map
            : name.SequenceEqual("line"u8) ? Field.Line
            : name.SequenceEqual("column"u8) ? Field.Column
            : Field.Other;
    }

    // An escaped name unescaped, or false when it is longer than any field's or holds an escape no field
    // name could: one that is not "\u", or one past a single byte. The reader has already checked each
    // escape is well formed: a backslash and one character, or "\u" and four hex digits.
    private static bool TryUnescapeName(ReadOnlySpan<byte> escaped, Span<byte> into, out int length)
    {
        length = 0;
        for (var i = 0; i < escaped.Length; length++)
        {
            if (length == into.Length)
            {
                return false;
            }

            if (escaped[i] != (byte)'\\')
            {
                into[length] = escaped[i++];
                continue;
            }

            if (escaped[i + 1] != (byte)'u'
                || !byte.TryParse(escaped.Slice(i + 2, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var letter))
            {
                return false;
            }

            into[length] = letter;
            i += 6;
        }

        return true;
    }
}
