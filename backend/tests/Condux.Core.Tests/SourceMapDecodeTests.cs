using System.Text;
using Condux.Core.SourceMaps;
using Xunit;

namespace Condux.Core.Tests;

/// <summary>
/// Decoding a hand-verified Source Map v3 (ADR-0028). The mappings "AAAA,CAEA;KAAGA" exercises VLQ
/// sign/delta semantics, the per-line generated-column reset with persistent source/line/column/name
/// accumulators, name + sourcesContent resolution, and gap/out-of-range -> null.
/// </summary>
public sealed class SourceMapDecodeTests
{
    private const string Json = """
    {
      "version": 3,
      "sources": ["src/app.ts"],
      "sourcesContent": ["const a = 1;\nfunction boom() {\n  throw new Error();\n}\n"],
      "names": ["boom"],
      "mappings": "AAAA,CAEA;KAAGA"
    }
    """;

    private static SourceMap Map() => SourceMap.Parse(Encoding.UTF8.GetBytes(Json))!;

    [Fact]
    public void First_segment_maps_to_the_first_source_line()
    {
        var pos = Map().OriginalPositionFor(0, 0);
        Assert.NotNull(pos);
        Assert.Equal("src/app.ts", pos!.Source);
        Assert.Equal(0, pos.Line);
        Assert.Equal(0, pos.Column);
        Assert.Null(pos.Name);
        Assert.Equal("const a = 1;", pos.SourceLine);
    }

    [Fact]
    public void Second_segment_uses_a_delta_encoded_original_line()
    {
        var pos = Map().OriginalPositionFor(0, 1);
        Assert.Equal(2, pos!.Line); // 0 + delta 2
        Assert.Equal(0, pos.Column);
        Assert.Equal("  throw new Error();", pos.SourceLine);
    }

    [Fact]
    public void A_column_inside_a_segment_resolves_to_that_segment()
    {
        // Column 5 on line 0 falls within the second segment (which starts at column 1), not a new one.
        var pos = Map().OriginalPositionFor(0, 5);
        Assert.Equal(2, pos!.Line);
        Assert.Equal(0, pos.Column);
    }

    [Fact]
    public void Accumulators_persist_across_the_line_reset_and_resolve_the_name()
    {
        // Line 1 resets the generated column to 0 but keeps the source/line/column accumulators; this
        // segment also carries the optional name field.
        var pos = Map().OriginalPositionFor(1, 5);
        Assert.Equal("src/app.ts", pos!.Source);
        Assert.Equal(2, pos.Line);
        Assert.Equal(3, pos.Column); // 0 + delta 3
        Assert.Equal("boom", pos.Name);
        Assert.Equal("  throw new Error();", pos.SourceLine);
    }

    [Fact]
    public void A_column_before_the_first_segment_has_no_mapping() =>
        Assert.Null(Map().OriginalPositionFor(1, 0)); // the first segment on line 1 starts at column 5

    [Fact]
    public void An_out_of_range_line_has_no_mapping() => Assert.Null(Map().OriginalPositionFor(2, 0));

    [Fact]
    public void An_index_map_is_not_supported_yet() =>
        Assert.Null(SourceMap.Parse(Encoding.UTF8.GetBytes("""{"version":3,"sections":[]}""")));

    // The separator counts come from a string a caller uploaded, so without a bound a map that is
    // almost all separators costs orders of magnitude more memory than it took to send. Refusing reads
    // to the symbolicator as a map it cannot use, which is a frame left alone rather than an error.
    // Both separators, because they allocate different things: the semicolons decide how many generated
    // lines get a list, the commas how many segments those lists hold. A real map is routinely lopsided,
    // so a bound on one of them waves the other through.
    [Theory]
    [InlineData(';')]
    [InlineData(',')]
    public void A_mappings_string_of_nothing_but_separators_is_refused_rather_than_decoded(char separator)
    {
        var mappings = new string(separator, 2_000_000);

        Assert.Null(SourceMap.Parse(Encoding.UTF8.GetBytes($$"""{"version":3,"mappings":"{{mappings}}"}""")));
    }

    // The other half, and the half that decides whether the bounds are set anywhere near the truth.
    // Measured across 7,152 maps from this repository's own bundler output and its installed packages,
    // the largest had 26,428 generated lines and the largest had 222,248 segments. Both shapes below
    // are past those, so a build larger than anything this repository has seen still symbolicates.
    [Fact]
    public void A_map_with_more_lines_than_any_real_build_still_decodes()
    {
        var mappings = string.Join(';', Enumerable.Repeat("AAAA", 30_000));

        var map = SourceMap.Parse(Encoding.UTF8.GetBytes(MapOf(mappings)));

        Assert.NotNull(map);
        Assert.Equal(0, map.OriginalPositionFor(29_999, 0)!.Column);
    }

    [Fact]
    public void A_map_with_more_segments_on_one_line_than_any_real_build_still_decodes()
    {
        var mappings = string.Join(',', Enumerable.Repeat("AAAA", 250_000));

        var map = SourceMap.Parse(Encoding.UTF8.GetBytes(MapOf(mappings)));

        Assert.NotNull(map);
        Assert.NotNull(map.OriginalPositionFor(0, 0));
    }

    [Fact]
    public void A_generated_line_with_no_segments_has_no_mapping()
    {
        // Line 1 is empty: two separators with nothing between them.
        const string json = """{"version":3,"sources":["a.js"],"names":[],"mappings":"AAAA;;AAAA"}""";
        var map = SourceMap.Parse(Encoding.UTF8.GetBytes(json))!;

        Assert.Null(map.OriginalPositionFor(1, 0));
        Assert.NotNull(map.OriginalPositionFor(2, 0));
    }

    [Fact]
    public void A_negative_vlq_delta_decodes_with_the_sign_bit()
    {
        // "K" is +5, "D" is -1 (index 3 = 0b000011: value bit 1, sign bit 1), so the original column
        // moves 0 -> 5 -> 4, exercising the sign path.
        const string json = """{"version":3,"sources":["a.js"],"names":[],"mappings":"AAAK,CAAD"}""";
        var map = SourceMap.Parse(Encoding.UTF8.GetBytes(json))!;
        Assert.Equal(5, map.OriginalPositionFor(0, 0)!.Column);
        Assert.Equal(4, map.OriginalPositionFor(0, 1)!.Column);
    }

    private static string MapOf(string mappings) =>
        $$"""{"version":3,"sources":["a.js"],"names":[],"mappings":"{{mappings}}"}""";
}
