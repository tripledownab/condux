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
}
