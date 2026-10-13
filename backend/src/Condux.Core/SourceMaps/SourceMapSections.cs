using System.Text.Json;

namespace Condux.Core.SourceMaps;

/// <summary>
/// The index map half of Source Map v3 (ECMA-426, "Index source map", DecodeIndexSourceMap): a
/// "sections" array whose entries each place an embedded plain map at an offset in the generated file.
/// Each section is kept as its own <see cref="SourceMap"/> and found by binary search on its offset,
/// which the specification's own note allows instead of concatenating the mappings.
/// </summary>
internal static class SourceMapSections
{
    internal readonly record struct Section(int Line, int Column, SourceMap Map);

    /// <summary>Whether a "sections" array decodes within the same budget as one plain map. The upload
    /// check asks this, so an index map over budget is refused when it is sent. One with no usable
    /// section is accepted, though <see cref="SourceMap.Parse"/> reads nothing from it: Turbopack writes
    /// empty index maps for its own build transforms, and the upload CLI sends every map it finds, so
    /// refusing them failed the CLI on any such build.</summary>
    public static bool IsDecodable(JsonElement sections) => Plan(sections) is not null;

    /// <summary>The decoded sections in offset order, or null when the array cannot be decoded at all
    /// (not an array, a section whose offset or map is not an object, or past the decode budget).</summary>
    public static Section[]? Parse(JsonElement sections)
    {
        if (Plan(sections) is not { } plan)
        {
            return null;
        }

        var decoded = new List<Section>(plan.Count);
        foreach (var (line, column, map) in plan)
        {
            if (SourceMap.ToPlain(map) is { } plain)
            {
                decoded.Add(new Section(line, column, plain));
            }
        }

        return [.. decoded];
    }

    /// <summary>The original position for a generated (line, column), both 0-based, in the last section
    /// starting at or before it. A section's column offset applies to its first line only, as in the
    /// specification's decoding, which adds it only to mappings on the section's line 0.</summary>
    public static OriginalPosition? Find(Section[] sections, int line, int column)
    {
        int lo = 0, hi = sections.Length - 1, found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (Compare(sections[mid].Line, sections[mid].Column, line, column) <= 0)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        if (found < 0)
        {
            return null;
        }

        var section = sections[found];
        var localLine = line - section.Line;
        return section.Map.OriginalPositionFor(localLine, localLine == 0 ? column - section.Column : column);
    }

    // The sections a decode would read, with their offsets, measured but not decoded. Following the
    // specification: a section that is not an object is skipped, an offset or map that is not an object
    // fails the whole map, an offset field that is not an integral number reads as 0, and a section whose
    // map has no "mappings" string (a nested index map among them) is skipped, as DecodeSourceMap
    // refuses it. A section that starts before the previous one is skipped too: the specification lets
    // a consumer report that, and the binary search in Find is only right over ordered offsets.
    private static List<(int Line, int Column, JsonElement Map)>? Plan(JsonElement sections)
    {
        if (sections.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var plan = new List<(int, int, JsonElement)>();
        long lines = 0, segments = 0;
        int previousLine = 0, previousColumn = 0;
        foreach (var section in sections.EnumerateArray())
        {
            if (section.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!section.TryGetProperty("offset", out var offset) || offset.ValueKind != JsonValueKind.Object
                || !section.TryGetProperty("map", out var map) || map.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var line = ReadOffset(offset, "line");
            var column = ReadOffset(offset, "column");
            if (Compare(line, column, previousLine, previousColumn) < 0
                || !map.TryGetProperty("mappings", out var mappings) || mappings.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var (sectionLines, sectionSegments) = SourceMapMappings.Measure(mappings.GetString()!);
            lines += sectionLines;
            segments += sectionSegments;
            if (!SourceMapMappings.Fits((lines, segments)))
            {
                return null;
            }

            plan.Add((line, column, map));
            (previousLine, previousColumn) = (line, column);
        }

        return plan;
    }

    // Integral as the specification reads it, so 3.0 is 3, and 2.5 or an infinity read as 0 as it says.
    // A negative one is read as 0 as well, since no generated position precedes the start of the file,
    // and a finite one past what a position can hold is kept at the largest, out of reach of every
    // frame, rather than wrapped or read as 0.
    private static int ReadOffset(JsonElement offset, string name) =>
        offset.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out var number) && double.IsFinite(number)
            && number == Math.Floor(number) && number >= 0
            ? (int)Math.Min(number, int.MaxValue)
            : 0;

    private static int Compare(int line, int column, int otherLine, int otherColumn) =>
        line != otherLine ? line.CompareTo(otherLine) : column.CompareTo(otherColumn);
}
