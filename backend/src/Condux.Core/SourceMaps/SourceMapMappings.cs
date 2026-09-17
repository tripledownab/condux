namespace Condux.Core.SourceMaps;

/// <summary>
/// The Source Map v3 "mappings" string: what it is allowed to cost, and how it decodes into one
/// column-ordered segment table per generated line. Separate from <see cref="SourceMap"/> because this
/// is the half that turns an uploaded string into objects, and so the half whose size has to be
/// answered for.
/// </summary>
internal static class SourceMapMappings
{
    // Two bounds because the decode allocates two ways and one string can be large in either. Measured
    // across 7,152 maps from this repository's own bundler output and its installed packages, the
    // largest had 26,428 generated lines and the largest had 222,248 segments. They are not the same
    // map: the second-largest by segments holds 195,113 of them on 71 lines, which is exactly what a
    // line bound alone would wave through. Both counts come from separators in an uploaded string, so
    // without them a map built to be expensive turns a 25MiB upload into an object per few bytes.
    private const int MaxGeneratedLines = 100_000;
    private const int MaxSegments = 500_000;

    // Fields 2..5 of a segment persist across the whole file; a source index of -1 marks a "gap" segment
    // (one VLQ field, a generated column with no original mapping), so a query there resolves to null.
    internal readonly record struct Segment(
        int GeneratedColumn, int SourceIndex, int OriginalLine, int OriginalColumn, int NameIndex);

    /// <summary>Whether a mappings string decodes to as many lines and segments as a real build
    /// produces. Counted rather than split, because the separators bound everything the decode would
    /// then allocate and reading them costs two passes and no objects.</summary>
    public static bool IsDecodable(string mappings)
    {
        var span = mappings.AsSpan();
        var lines = span.Count(';') + 1;
        // An upper bound, not the exact figure: every segment is followed by a separator or the end.
        var segments = lines + span.Count(',');
        return lines <= MaxGeneratedLines && segments <= MaxSegments;
    }

    /// <summary>One segment table per generated line, or null for a line that carries no segments,
    /// which is most of them in a real map: an empty list per line costs several times the line it
    /// stands for.</summary>
    public static List<Segment>?[] Decode(string mappings)
    {
        var lineStrings = mappings.Split(';');
        var lines = new List<Segment>?[lineStrings.Length];
        // Fields 2..5 accumulate across the whole file; the generated column resets to 0 each line.
        int sourceIndex = 0, originalLine = 0, originalColumn = 0, nameIndex = 0;
        Span<int> fields = stackalloc int[5];

        for (var line = 0; line < lineStrings.Length; line++)
        {
            if (lineStrings[line].Length == 0)
            {
                continue;
            }

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

    /// <summary>The segment with the largest GeneratedColumn at or before the query column, or null
    /// when the line starts after it. Segments are column-ordered, so this is a binary search.</summary>
    public static Segment? FindSegment(List<Segment> segments, int generatedColumn)
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
}
