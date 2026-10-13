using System.Text;
using Condux.Core.SourceMaps;
using Xunit;

namespace Condux.Core.Tests;

/// <summary>
/// Index source maps (ECMA-426, "Index source map", DecodeIndexSourceMap): a generated position resolves
/// through the section that starts at or before it, with the section's line offset applied and its
/// column offset applied on the section's first line only, as the specification's decoding does.
/// </summary>
public sealed class SourceMapSectionsTests
{
    // "AAAA" maps generated column 0 to the source's line 0, column 0; "AACA" to line 1.
    private static string Section(int line, int column, string source, string mappings) =>
        $$$"""{"offset":{"line":{{{line}}},"column":{{{column}}}},"map":{"version":3,"sources":["{{{source}}}"],"names":[],"mappings":"{{{mappings}}}"}}""";

    private static SourceMap? Parse(params string[] sections) =>
        SourceMap.Parse(Encoding.UTF8.GetBytes($$"""{"version":3,"sections":[{{string.Join(",", sections)}}]}"""));

    [Fact]
    public void Resolves_each_position_through_its_own_section()
    {
        var map = Parse(Section(0, 0, "a.ts", "AAAA"), Section(10, 0, "b.ts", "AACA"))!;

        Assert.Equal(("a.ts", 0), (map.OriginalPositionFor(0, 0)?.Source, map.OriginalPositionFor(0, 0)?.Line));
        Assert.Equal(("b.ts", 1), (map.OriginalPositionFor(10, 0)?.Source, map.OriginalPositionFor(10, 0)?.Line));
    }

    /// <summary>The column offset moves the section's first line and no other.</summary>
    [Fact]
    public void Applies_the_column_offset_to_the_first_line_only()
    {
        // The section's own line 0 column 0 sits at generated (5, 20); its line 1 column 0 at (6, 0).
        var map = Parse(Section(0, 0, "a.ts", "AAAA"), Section(5, 20, "b.ts", "AAAA;AACA"))!;

        Assert.Equal("b.ts", map.OriginalPositionFor(5, 20)?.Source);
        Assert.Equal(("b.ts", 1), (map.OriginalPositionFor(6, 0)?.Source, map.OriginalPositionFor(6, 0)?.Line));
        // Before the section starts, the position belongs to the section before it.
        Assert.Null(map.OriginalPositionFor(5, 19));
    }

    /// <summary>
    /// The first line's column is measured from the offset: with segments at the section's columns 0 and
    /// 5, generated column 23 is the section's column 3, so it falls on the first segment, not the second.
    /// </summary>
    [Fact]
    public void Measures_the_first_line_from_the_column_offset()
    {
        // "AAAA,KACA": column 0 maps to line 0, column 5 to line 1.
        var map = Parse(Section(0, 0, "a.ts", "AAAA"), Section(5, 20, "b.ts", "AAAA,KACA"))!;

        Assert.Equal(0, map.OriginalPositionFor(5, 23)?.Line);
        Assert.Equal(1, map.OriginalPositionFor(5, 25)?.Line);
    }

    [Fact]
    public void Has_no_mapping_before_the_first_section() =>
        Assert.Null(Parse(Section(3, 0, "a.ts", "AAAA"))!.OriginalPositionFor(2, 0));

    /// <summary>
    /// The specification lets a consumer report a section that starts before the previous one; this
    /// one skips it, since a binary search over the offsets is only right when they are ordered.
    /// </summary>
    [Fact]
    public void Skips_a_section_that_starts_before_the_previous_one()
    {
        var map = Parse(Section(5, 0, "a.ts", "AAAA"), Section(2, 0, "b.ts", "AAAA"))!;

        Assert.Null(map.OriginalPositionFor(2, 0));
        Assert.Equal("a.ts", map.OriginalPositionFor(5, 0)?.Source);
    }

    /// <summary>
    /// A section that is not an object, or whose map has no mappings (a nested index map among them), is
    /// skipped; an offset or map that is not an object fails the whole map, as the specification throws.
    /// </summary>
    [Fact]
    public void Skips_what_the_specification_skips_and_refuses_what_it_throws_on()
    {
        var nested = """{"offset":{"line":1,"column":0},"map":{"version":3,"sections":[]}}""";
        var map = Parse("7", nested, Section(2, 0, "a.ts", "AAAA"))!;

        Assert.Null(map.OriginalPositionFor(1, 0));
        Assert.Equal("a.ts", map.OriginalPositionFor(2, 0)?.Source);
        Assert.Null(Parse("""{"offset":1,"map":{}}""", Section(0, 0, "a.ts", "AAAA")));
        Assert.Null(Parse("""{"offset":{"line":0},"map":"x"}""", Section(0, 0, "a.ts", "AAAA")));
    }

    /// <summary>An offset that is not a usable whole number reads as 0.</summary>
    [Fact]
    public void Reads_an_unusable_offset_as_zero() =>
        Assert.Equal(
            "a.ts",
            SourceMap.Parse(Encoding.UTF8.GetBytes(
                """{"version":3,"sections":[{"offset":{"line":"x","column":-4},"map":{"version":3,"sources":["a.ts"],"mappings":"AAAA"}}]}"""))
            ?.OriginalPositionFor(0, 0)?.Source);

    /// <summary>
    /// As the specification's ParseSourceMap reads it, "sections" makes an index map whatever else the
    /// object holds, so its own "mappings" are never read.
    /// </summary>
    [Fact]
    public void Reads_a_map_with_both_fields_by_its_sections()
    {
        var map = SourceMap.Parse(Encoding.UTF8.GetBytes(
            $$"""{"version":3,"sources":["top.ts"],"mappings":"AAAA","sections":[{{Section(0, 0, "a.ts", "AAAA")}}]}"""));

        Assert.Equal("a.ts", map?.OriginalPositionFor(0, 0)?.Source);
    }

    /// <summary>Integral as the specification reads it, so 3.0 is 3, not 0.</summary>
    [Fact]
    public void Reads_an_integral_offset_written_with_a_fraction() =>
        Assert.Equal(
            "a.ts",
            SourceMap.Parse(Encoding.UTF8.GetBytes(
                """{"version":3,"sections":[{"offset":{"line":0,"column":0},"map":{"version":3,"sources":["a.ts"],"mappings":"AAAA"}},{"offset":{"line":3.0,"column":0},"map":{"version":3,"sources":["b.ts"],"mappings":"AAAA"}}]}"""))
            ?.OriginalPositionFor(0, 0)?.Source);

    /// <summary>A fractional offset is not integral, so the specification reads it as 0.</summary>
    [Fact]
    public void Reads_a_fractional_offset_as_zero() =>
        Assert.Equal(
            "a.ts",
            SourceMap.Parse(Encoding.UTF8.GetBytes(
                """{"version":3,"sections":[{"offset":{"line":2.5,"column":0},"map":{"version":3,"sources":["a.ts"],"mappings":"AAAA"}}]}"""))
            ?.OriginalPositionFor(0, 0)?.Source);

    /// <summary>An offset past any position is out of reach, not read as 0.</summary>
    [Fact]
    public void Keeps_a_huge_offset_out_of_reach() =>
        Assert.Null(SourceMap.Parse(Encoding.UTF8.GetBytes(
                """{"version":3,"sections":[{"offset":{"line":10000000000,"column":0},"map":{"version":3,"sources":["a.ts"],"mappings":"AAAA"}}]}"""))
            ?.OriginalPositionFor(0, 0));

    [Fact]
    public void Refuses_an_index_map_with_no_usable_section()
    {
        Assert.Null(Parse());
        Assert.Null(Parse("""{"offset":{"line":0},"map":{"version":3,"sections":[]}}"""));
    }

    /// <summary>The decode budget holds for the whole map, summed across its sections.</summary>
    [Fact]
    public void Refuses_sections_that_together_exceed_the_decode_budget()
    {
        var big = new string(';', 60_000);

        Assert.NotNull(Parse(Section(0, 0, "a.ts", big)));
        Assert.Null(Parse(Section(0, 0, "a.ts", big), Section(70_000, 0, "b.ts", big)));
    }
}
