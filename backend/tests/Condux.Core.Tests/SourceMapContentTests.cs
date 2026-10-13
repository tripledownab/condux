using System.Text;
using Condux.Core.SourceMaps;
using Xunit;

namespace Condux.Core.Tests;

/// <summary>The upload sanity check (ADR-0028): accept a v3 source map (plain or index), reject anything
/// that is not one, so the store never holds arbitrary bytes labelled as a map.</summary>
public sealed class SourceMapContentTests
{
    private static bool Check(string json) => SourceMapContent.LooksLikeSourceMap(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void Accepts_a_standard_v3_source_map() =>
        Assert.True(Check("""{"version":3,"file":"app.js","sources":["app.ts"],"mappings":"AAAA"}"""));

    [Fact]
    public void Accepts_an_index_map_with_a_usable_section() =>
        Assert.True(Check("""{"version":3,"sections":[{"offset":{"line":0,"column":0},"map":{"version":3,"mappings":"AAAA"}}]}"""));

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{"version":2,"mappings":"AAAA"}""")]     // wrong version
    [InlineData("""{"mappings":"AAAA"}""")]                  // no version
    [InlineData("""{"version":3}""")]                        // no mappings or sections
    [InlineData("""{"version":"3","mappings":"AAAA"}""")]   // version not a number
    [InlineData("[]")]                                        // not an object
    [InlineData("")]                                          // empty
    public void Rejects_non_source_maps(string json) => Assert.False(Check(json));

    // The upload gate asks the decoder's own question, so a map that would be stored and then silently
    // symbolicate nothing is refused here with a reason instead.
    [Fact]
    public void Rejects_a_map_the_decoder_would_refuse()
    {
        var mappings = new string(';', 2_000_000);

        Assert.False(Check($$"""{"version":3,"mappings":"{{mappings}}"}"""));
    }

    // An index map is held to the same budget, counted across its sections: each section below is
    // within it alone, and together they are not.
    [Fact]
    public void Rejects_an_index_map_whose_sections_together_the_decoder_would_refuse()
    {
        var section = $$$"""{"offset":{"line":0,"column":0},"map":{"version":3,"mappings":"{{{new string(';', 60_000)}}}"}}""";

        Assert.True(Check($$"""{"version":3,"sections":[{{section}}]}"""));
        Assert.False(Check($$"""{"version":3,"sections":[{{section}},{{section}}]}"""));
    }

    // Judged as the decoder reads it: "sections" makes an index map whatever else the object holds, so
    // over-budget mappings beside a usable section pass, and good mappings beside a broken one do not.
    [Fact]
    public void Judges_a_map_with_both_fields_by_its_sections()
    {
        var mappings = new string(';', 2_000_000);
        var section = """{"offset":{"line":0,"column":0},"map":{"version":3,"mappings":"AAAA"}}""";

        Assert.True(Check($$"""{"version":3,"mappings":"{{mappings}}","sections":[{{section}}]}"""));
        Assert.False(Check("""{"version":3,"mappings":"AAAA","sections":[{"offset":1,"map":{}}]}"""));
    }

    // Turbopack writes empty index maps for its own build transforms, and the upload CLI sends every map
    // it finds, so one with no usable section is accepted: refusing it failed the CLI on such builds.
    [Theory]
    [InlineData("""{"version":3,"sources":[],"sections":[]}""")]
    [InlineData("""{"version":3,"sections":[{"offset":{"line":0},"map":{"version":3,"sections":[]}}]}""")]
    public void Accepts_an_index_map_with_no_usable_section(string json) => Assert.True(Check(json));
}
