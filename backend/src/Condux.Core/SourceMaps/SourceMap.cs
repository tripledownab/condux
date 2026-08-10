using System.Text.Json;

namespace Condux.Core.SourceMaps;

/// <summary>
/// A parsed Source Map v3 (ECMA-426), decoded so a generated (line, column) resolves to its original
/// source position for read-time symbolication (ADR-0028). Plain maps only: an index map (with a
/// "sections" array instead of "mappings") returns null from <see cref="Parse"/> (a follow-up).
/// </summary>
public sealed class SourceMap
{
    // Fields 2..5 of a segment persist across the whole file; a source index of -1 marks a "gap" segment
    // (one VLQ field, a generated column with no original mapping), so a query there resolves to null.
    private readonly record struct Segment(
        int GeneratedColumn, int SourceIndex, int OriginalLine, int OriginalColumn, int NameIndex);

    private readonly string[] sources;
    private readonly string?[] sourcesContent;
    private readonly string[] names;
    private readonly List<Segment>[] lines;

    private SourceMap(string[] sources, string?[] sourcesContent, string[] names, List<Segment>[] lines)
    {
        this.sources = sources;
        this.sourcesContent = sourcesContent;
        this.names = names;
        this.lines = lines;
    }

    public static SourceMap? Parse(byte[] json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("mappings", out var mappings)
                || mappings.ValueKind != JsonValueKind.String)
            {
                return null; // not a plain v3 map (e.g. an index map with "sections")
            }

            return new SourceMap(
                ReadStrings(root, "sources"),
                ReadNullableStrings(root, "sourcesContent"),
                ReadStrings(root, "names"),
                DecodeMappings(mappings.GetString()!));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The original position for a generated (line, column) — both 0-based — or null when the
    /// map has no mapping there (out of range, before the first segment, or a gap segment).</summary>
    public OriginalPosition? OriginalPositionFor(int generatedLine, int generatedColumn)
    {
        if (generatedLine < 0 || generatedLine >= lines.Length)
        {
            return null;
        }

        var segment = FindSegment(lines[generatedLine], generatedColumn);
        if (segment is not { SourceIndex: >= 0 } s || s.SourceIndex >= sources.Length)
        {
            return null;
        }

        var name = s.NameIndex >= 0 && s.NameIndex < names.Length ? names[s.NameIndex] : null;
        var content = s.SourceIndex < sourcesContent.Length ? sourcesContent[s.SourceIndex] : null;
        var sourceLine = content is null ? null : LineAt(content, s.OriginalLine);
        return new OriginalPosition(sources[s.SourceIndex], s.OriginalLine, s.OriginalColumn, name, sourceLine);
    }

    // The segment with the largest GeneratedColumn <= the query column (segments are column-ordered).
    private static Segment? FindSegment(List<Segment> segments, int generatedColumn)
    {
        int lo = 0, hi = segments.Count - 1, found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (segments[mid].GeneratedColumn <= generatedColumn)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return found < 0 ? null : segments[found];
    }

    private static List<Segment>[] DecodeMappings(string mappings)
    {
        var lineStrings = mappings.Split(';');
        var lines = new List<Segment>[lineStrings.Length];
        // Fields 2..5 accumulate across the whole file; the generated column resets to 0 each line.
        int sourceIndex = 0, originalLine = 0, originalColumn = 0, nameIndex = 0;
        Span<int> fields = stackalloc int[5];

        for (var line = 0; line < lineStrings.Length; line++)
        {
            var segments = new List<Segment>();
            var generatedColumn = 0;
            foreach (var segStr in lineStrings[line].Split(','))
            {
                if (segStr.Length == 0)
                {
                    continue;
                }

                var n = Base64Vlq.Decode(segStr, fields);
                if (n < 1)
                {
                    continue; // malformed segment, skip
                }

                generatedColumn += fields[0];
                if (n < 4)
                {
                    segments.Add(new Segment(generatedColumn, -1, 0, 0, -1)); // gap: no original position
                    continue;
                }

                sourceIndex += fields[1];
                originalLine += fields[2];
                originalColumn += fields[3];
                var name = -1;
                if (n >= 5)
                {
                    nameIndex += fields[4];
                    name = nameIndex;
                }

                segments.Add(new Segment(generatedColumn, sourceIndex, originalLine, originalColumn, name));
            }

            lines[line] = segments;
        }

        return lines;
    }

    // The 0-based line of a sourcesContent blob, without a trailing CR (source maps use \n line endings).
    private static string? LineAt(string content, int lineIndex)
    {
        if (lineIndex < 0)
        {
            return null;
        }

        var start = 0;
        for (var i = 0; i < lineIndex; i++)
        {
            var nl = content.IndexOf('\n', start);
            if (nl < 0)
            {
                return null;
            }

            start = nl + 1;
        }

        var end = content.IndexOf('\n', start);
        return (end < 0 ? content[start..] : content[start..end]).TrimEnd('\r');
    }

    private static string[] ReadStrings(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            list.Add(item.ValueKind == JsonValueKind.String ? item.GetString()! : "");
        }

        return [.. list];
    }

    private static string?[] ReadNullableStrings(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<string?>();
        foreach (var item in array.EnumerateArray())
        {
            list.Add(item.ValueKind == JsonValueKind.String ? item.GetString() : null);
        }

        return [.. list];
    }
}
