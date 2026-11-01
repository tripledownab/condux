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
    private readonly HashSet<int> ignored;

    private readonly List<SourceMapMappings.Segment>?[] lines;

    // Set for an index map, whose own sources, names and lines stay empty.
    private readonly SourceMapSections.Section[]? sections;

    private SourceMap(
        string[] sources,
        string?[] sourcesContent,
        string[] names,
        HashSet<int> ignored,
        List<SourceMapMappings.Segment>?[] lines)
    {
        this.sources = sources;
        this.sourcesContent = sourcesContent;
        this.names = names;
        this.ignored = ignored;
        this.lines = lines;
    }

    private SourceMap(SourceMapSections.Section[] sections)
        : this([], [], [], [], [])
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
            if (SourceMapJson.Scan(json) is not { } root)
            {
                return null;
            }

            if (root.HasSections)
            {
                return SourceMapSections.Parse(json, root.Sections) is { Length: > 0 } decoded ? new SourceMap(decoded) : null;
            }

            return ToPlain(json, root);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A plain map decoded from the fields the scan noted, or null without a decodable
    /// "mappings" string. Throws <see cref="JsonException"/> when a string it reads is not valid text.</summary>
    internal static SourceMap? ToPlain(byte[] json, SourceMapFields map)
    {
        if (map.Mappings is not { } mappings || !SourceMapMappings.Fits(SourceMapJson.MeasureMappings(json, mappings)))
        {
            return null;
        }

        // Rollup 4 (and a Vite build on it) writes only the deprecated name, which ECMA-426 notes browsers
        // read when ignoreList is not present.
        return new SourceMap(
            [.. SourceMapJson.ToStrings(json, map.Sources).Select(source => source ?? "")],
            SourceMapJson.ToStrings(json, map.SourcesContent),
            [.. SourceMapJson.ToStrings(json, map.Names).Select(name => name ?? "")],
            SourceMapJson.ToIndexes(json, map.IgnoreList is not null ? map.IgnoreList : map.XGoogleIgnoreList),
            SourceMapMappings.Decode(SourceMapJson.ToMappings(json, mappings)));
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
        return new OriginalPosition(
            sources[s.SourceIndex], s.OriginalLine, s.OriginalColumn, name, sourceLine, ignored.Contains(s.SourceIndex));
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
}
