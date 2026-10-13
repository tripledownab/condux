using System.Text.Json;

namespace Condux.Core.SourceMaps;

/// <summary>
/// A parsed Source Map v3 (ECMA-426), decoded so a generated (line, column) resolves to its original
/// source position for read-time symbolication (ADR-0028). A plain map decodes its own "mappings"; an
/// index map ("sections") holds one plain map per section, decoded by <see cref="SourceMapSections"/>.
/// </summary>
public sealed class SourceMap
{
    private readonly string[] sources;
    private readonly string?[] sourcesContent;
    private readonly string[] names;

    private readonly List<SourceMapMappings.Segment>?[] lines;

    // Set for an index map, whose own sources, names and lines stay empty.
    private readonly SourceMapSections.Section[]? sections;

    private SourceMap(
        string[] sources, string?[] sourcesContent, string[] names, List<SourceMapMappings.Segment>?[] lines)
    {
        this.sources = sources;
        this.sourcesContent = sourcesContent;
        this.names = names;
        this.lines = lines;
    }

    private SourceMap(SourceMapSections.Section[] sections)
        : this([], [], [], [])
    {
        this.sections = sections;
    }

    /// <summary>The decoded map, or null when it is not a source map this can read: not JSON, neither
    /// form, past the decode budget, or an index map none of whose sections decodes. As in the
    /// specification's ParseSourceMap, a "sections" field makes it an index map whatever else it holds.
    /// </summary>
    public static SourceMap? Parse(byte[] json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (root.TryGetProperty("sections", out var sections))
            {
                return SourceMapSections.Parse(sections) is { Length: > 0 } decoded ? new SourceMap(decoded) : null;
            }

            return ToPlain(root);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A plain map from its JSON object, or null without a decodable "mappings" string.</summary>
    internal static SourceMap? ToPlain(JsonElement root)
    {
        if (!root.TryGetProperty("mappings", out var mappings)
            || mappings.ValueKind != JsonValueKind.String
            || !SourceMapMappings.IsDecodable(mappings.GetString()!))
        {
            return null;
        }

        return new SourceMap(
            ReadStrings(root, "sources"),
            ReadNullableStrings(root, "sourcesContent"),
            ReadStrings(root, "names"),
            SourceMapMappings.Decode(mappings.GetString()!));
    }

    /// <summary>The original position for a generated (line, column) — both 0-based — or null when the
    /// map has no mapping there (out of range, before the first segment, or a gap segment).</summary>
    public OriginalPosition? OriginalPositionFor(int generatedLine, int generatedColumn)
    {
        if (sections is not null)
        {
            return SourceMapSections.Find(sections, generatedLine, generatedColumn);
        }

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
