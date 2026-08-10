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
    public void Accepts_an_index_map_with_sections() =>
        Assert.True(Check("""{"version":3,"sections":[]}"""));

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{"version":2,"mappings":"AAAA"}""")]     // wrong version
    [InlineData("""{"mappings":"AAAA"}""")]                  // no version
    [InlineData("""{"version":3}""")]                        // no mappings or sections
    [InlineData("""{"version":"3","mappings":"AAAA"}""")]   // version not a number
    [InlineData("[]")]                                        // not an object
    [InlineData("")]                                          // empty
    public void Rejects_non_source_maps(string json) => Assert.False(Check(json));
}
