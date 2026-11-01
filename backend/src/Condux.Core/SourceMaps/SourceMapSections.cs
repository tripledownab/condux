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

    /// <summary>Whether an index map's plan decodes within the same budget as one plain map, measuring
    /// each planned section's mappings, which throws <see cref="System.Text.Json.JsonException"/> when they
    /// are not valid text. Only the plan the scan left is measured: that is the last "sections" array, the one
    /// a read uses.</summary>
    public static bool IsDecodable(byte[] json, List<PlannedSection>? plan)
    {
        if (plan is null)
        {
            return false;
        }

        long lines = 0, segments = 0;
        foreach (var section in plan)
        {
            var (sectionLines, sectionSegments) = SourceMapJson.MeasureMappings(json, section.Map.Mappings!.Value);
            lines += sectionLines;
            segments += sectionSegments;
            if (!SourceMapMappings.Fits((lines, segments)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The decoded sections in offset order, or null when the index map cannot be decoded at all
    /// (not an array, a section whose offset or map is not an object, more sections or more mappings than
    /// the decode budget). Throws <see cref="System.Text.Json.JsonException"/> as
    /// <see cref="IsDecodable"/> does.</summary>
    public static Section[]? Parse(byte[] json, List<PlannedSection>? plan)
    {
        if (!IsDecodable(json, plan))
        {
            return null;
        }

        var decoded = new List<Section>(plan!.Count);
        foreach (var (line, column, map) in plan)
        {
            if (SourceMap.ToPlain(json, map) is { } plain)
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

    /// <summary>
    /// Chooses the sections a decode would read as the scan offers them, by their shape alone: nothing is
    /// measured or read here, so an array a later "sections" key replaces fails nothing, and costs no more
    /// than the plan it held, which the line budget bounds.
    /// Following the specification: a section that is not an object is skipped (the scan offers only
    /// objects), an offset or map that is not an object fails the whole map, an offset field that is not an
    /// integral number reads as 0, and a section whose map has no "mappings" string (a nested index map
    /// among them) is skipped, as DecodeSourceMap refuses it. A section that starts before the previous one
    /// is skipped too: the specification lets a consumer report that, and the binary search in Find is only
    /// right over ordered offsets. Every section kept adds at least one generated line, so a plan with more
    /// sections than the line budget allows is refused as it grows, which bounds it without measuring.
    /// </summary>
    internal sealed class Planner
    {
        private readonly List<PlannedSection> planned = [];
        private int previousLine, previousColumn;

        /// <summary>Whether the index map cannot be decoded at all, after which nothing more is offered.</summary>
        public bool Refused { get; private set; }

        /// <summary>The sections to decode, in offset order, or null once refused.</summary>
        public List<PlannedSection>? Plan => Refused ? null : planned;

        /// <summary>Starts the plan again for a "sections" array that replaces an earlier one in the same map,
        /// reusing what the earlier plan held, so a key repeated however often costs one plan.</summary>
        public void Restart()
        {
            planned.Clear();
            (previousLine, previousColumn) = (0, 0);
            Refused = false;
        }

        /// <summary>One section, with a null map when its offset or map is missing or not an object.</summary>
        public void Offer(int line, int column, SourceMapFields? map)
        {
            if (map is not { } fields)
            {
                Refused = true;
                return;
            }

            if (Compare(line, column, previousLine, previousColumn) < 0 || fields.Mappings is null)
            {
                return;
            }

            // A section past the budget's count would add a line past the budget, so the plan is over it.
            if (planned.Count == SourceMapMappings.MaxGeneratedLines)
            {
                Refused = true;
                return;
            }

            planned.Add(new PlannedSection(line, column, fields));
            (previousLine, previousColumn) = (line, column);
        }
    }

    private static int Compare(int line, int column, int otherLine, int otherColumn) =>
        line != otherLine ? line.CompareTo(otherLine) : column.CompareTo(otherColumn);
}
