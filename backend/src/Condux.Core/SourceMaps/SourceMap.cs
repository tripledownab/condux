using System.Text.Json;

namespace Condux.Core.SourceMaps;

/// <summary>
/// A parsed Source Map v3 (ECMA-426), decoded so a generated (line, column) resolves to its original
/// source position for read-time symbolication (ADR-0028). Plain maps only: an index map (with a
/// "sections" array instead of "mappings") returns null from <see cref="Parse"/> (a follow-up).
/// </summary>
public sealed class SourceMap
{
    private readonly string[] sources;
    private readonly string?[] sourcesContent;
    private readonly string[] names;

    private readonly List<SourceMapMappings.Segment>?[] lines;

    private SourceMap(
        string[] sources, string?[] sourcesContent, string[] names, List<SourceMapMappings.Segment>?[] lines)
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

            if (!SourceMapMappings.IsDecodable(mappings.GetString()!))
            {
                return null;
            }

            return new SourceMap(
                ReadStrings(root, "sources"),
                ReadNullableStrings(root, "sourcesContent"),
                ReadStrings(root, "names"),
                SourceMapMappings.Decode(mappings.GetString()!));
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

        if (lines[generatedLine] is not { } segments)
        {
            return null; // a generated line with no segments
        }

        var segment = SourceMapMappings.FindSegment(segments, generatedColumn);
        if (segment is not { SourceIndex: >= 0 } s || s.SourceIndex >= sources.Length)
        {
            return null;
        }

        var name = s.NameIndex >= 0 && s.NameIndex < names.Length ? names[s.NameIndex] : null;
        var content = s.SourceIndex < sourcesContent.Length ? sourcesContent[s.SourceIndex] : null;
        var sourceLine = content is null ? null : LineAt(content, s.OriginalLine);
        return new OriginalPosition(sources[s.SourceIndex], s.OriginalLine, s.OriginalColumn, name, sourceLine);
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
