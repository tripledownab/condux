using System.Text.Json;

namespace Condux.Core.SourceMaps;

/// <summary>
/// Reads a Source Map v3 document (ECMA-426) token by token, in two steps: a scan that builds nothing but
/// notes where each field of a map lies (here), and a decode that reads, measures and checks the strings of
/// only the maps a caller uses (<c>SourceMapJson.Decode.cs</c>). A JSON document of the whole thing held an
/// index entry for every value, so a map padded with values it does not need cost many times its own size,
/// once to check at upload and again on every read. Only what a read uses is held to anything beyond being
/// well formed JSON, as a document held it: a field repeated in one object takes its last value, and a
/// string that is not valid text, where it is read, is malformed and reported as a
/// <see cref="JsonException"/>.
/// </summary>
internal static partial class SourceMapJson
{
    /// <summary>The root map, or null when the document does not open with an object, which is not read
    /// further. Throws <see cref="JsonException"/> when one that does is malformed JSON.</summary>
    public static SourceMapFields? Scan(byte[] json)
    {
        var reader = new Utf8JsonReader(json);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            return null;
        }

        var root = ScanMap(ref reader, isRoot: true);
        reader.Read(); // anything after the root object other than whitespace throws, as a document does
        return root;
    }

    // Positioned on a StartObject; returns positioned on its EndObject. A section's map is scanned the same
    // way without its own version or sections, which nothing reads.
    private static SourceMapFields ScanMap(ref Utf8JsonReader reader, bool isRoot)
    {
        var map = new SourceMapFields();
        SourceMapSections.Planner? planner = null; // one per root, so repeated "sections" keys share it
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var field = ToField(ref reader);
            reader.Read();
            switch (field)
            {
                case Field.Mappings:
                    map.Mappings = reader.TokenType == JsonTokenType.String ? ToRange(ref reader) : null;
                    break;
                case Field.Version when isRoot:
                    map.IsVersion3 = reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var v) && v == 3;
                    break;
                case Field.Sections when isRoot:
                    map.HasSections = true;
                    planner ??= new SourceMapSections.Planner();
                    map.Sections = reader.TokenType == JsonTokenType.StartArray ? ScanSections(ref reader, planner) : null;
                    break;
                case Field.Sources:
                    map.Sources = ToRange(ref reader);
                    break;
                case Field.SourcesContent:
                    map.SourcesContent = ToRange(ref reader);
                    break;
                case Field.Names:
                    map.Names = ToRange(ref reader);
                    break;
                case Field.IgnoreList:
                    map.IgnoreList = ToRange(ref reader);
                    break;
                case Field.XGoogleIgnoreList:
                    map.XGoogleIgnoreList = ToRange(ref reader);
                    break;
            }
            reader.Skip(); // whatever the value was not read as, and anything else, is passed over unbuilt
        }

        return map;
    }

    // Each section is offered to the planner as it is read, so sections no decode would use are dropped
    // as they pass, and what is kept is bounded by the line budget rather than by the document. A later
    // "sections" key starts the plan again, since the last one is the one read.
    private static List<PlannedSection>? ScanSections(ref Utf8JsonReader reader, SourceMapSections.Planner planner)
    {
        planner.Restart();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType == JsonTokenType.StartObject && !planner.Refused)
            {
                var (line, column, map) = ScanSection(ref reader);
                planner.Offer(line, column, map);
            }
            reader.Skip(); // a section that is not an object, and every section once refused, is passed over
        }

        return planner.Plan;
    }

    private static (int Line, int Column, SourceMapFields? Map) ScanSection(ref Utf8JsonReader reader)
    {
        int line = 0, column = 0;
        var offsetIsObject = false;
        SourceMapFields? map = null;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var field = ToField(ref reader);
            reader.Read();
            if (field == Field.Offset)
            {
                offsetIsObject = reader.TokenType == JsonTokenType.StartObject;
                (line, column) = offsetIsObject ? ScanOffset(ref reader) : (0, 0);
            }
            else if (field == Field.Map)
            {
                map = reader.TokenType == JsonTokenType.StartObject ? ScanMap(ref reader, isRoot: false) : null;
            }
            reader.Skip();
        }

        // An offset or map that is missing or not an object makes the whole index map unreadable.
        return (line, column, offsetIsObject ? map : null);
    }

    private static (int Line, int Column) ScanOffset(ref Utf8JsonReader reader)
    {
        int line = 0, column = 0;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var field = ToField(ref reader);
            reader.Read();
            if (field == Field.Line)
            {
                line = ToPosition(ref reader);
            }
            else if (field == Field.Column)
            {
                column = ToPosition(ref reader);
            }
            reader.Skip();
        }

        return (line, column);
    }

    // Integral as the specification reads it, so 3.0 is 3, and 2.5 or an infinity read as 0 as it says.
    // A negative one is read as 0 as well, since no generated position precedes the start of the file,
    // and a finite one past what a position can hold is kept at the largest, out of reach of every
    // frame, rather than wrapped or read as 0.
    private static int ToPosition(ref Utf8JsonReader reader) =>
        reader.TokenType == JsonTokenType.Number && reader.TryGetDouble(out var number) && double.IsFinite(number)
            && number == Math.Floor(number) && number >= 0
            ? (int)Math.Min(number, int.MaxValue)
            : 0;

    // Where the value under the reader lies, read past it; positioned on its last token, as Skip leaves it.
    private static JsonRange ToRange(ref Utf8JsonReader reader)
    {
        var start = (int)reader.TokenStartIndex;
        reader.Skip();
        return new JsonRange(start, (int)reader.BytesConsumed - start);
    }
}
