using System.Text;
using Condux.Core.SourceMaps;
using Xunit;

namespace Condux.Core.Tests;

/// <summary>
/// A map's <c>ignoreList</c> (ECMA-426, read with GetOptionalListOfArrayIndexes) marks the positions it
/// resolves to as third-party or bundler-generated. Two sources, one segment each on the first line:
/// column 0 maps to source 0 and column 1 to source 1.
/// </summary>
public sealed class SourceMapIgnoreListTests
{
    private static SourceMap Map(string ignoreList) => SourceMap.Parse(Encoding.UTF8.GetBytes(
        $$"""{"version":3,"sources":["src/app.ts","vendor/lib.js"],{{ignoreList}}"names":[],"mappings":"AAAA,CCAA"}"""))!;

    [Fact]
    public void A_listed_source_resolves_as_ignored_and_an_unlisted_one_does_not()
    {
        var map = Map("\"ignoreList\":[1],");
        Assert.False(map.OriginalPositionFor(0, 0)!.Ignored);
        Assert.Equal("vendor/lib.js", map.OriginalPositionFor(0, 1)!.Source);
        Assert.True(map.OriginalPositionFor(0, 1)!.Ignored);
    }

    [Fact]
    public void A_map_without_the_field_ignores_nothing() =>
        Assert.False(Map("").OriginalPositionFor(0, 1)!.Ignored);

    // Only integral, non-negative numbers count, and an integral number written with a fraction is one.
    [Theory]
    [InlineData("\"ignoreList\":[1.0],", true)]
    [InlineData("\"ignoreList\":[-1, 1.5, \"1\", null],", false)]
    [InlineData("\"ignoreList\":\"1\",", false)]
    [InlineData("\"ignoreList\":[9],", false)]
    public void Only_valid_indexes_count(string field, bool ignored) =>
        Assert.Equal(ignored, Map(field).OriginalPositionFor(0, 1)!.Ignored);

    // Rollup writes the deprecated name only; it counts when ignoreList is not present, and only then.
    [Theory]
    [InlineData("\"x_google_ignoreList\":[1],", true)]
    [InlineData("\"ignoreList\":[],\"x_google_ignoreList\":[1],", false)]
    public void The_deprecated_name_counts_only_without_the_standard_one(string fields, bool ignored) =>
        Assert.Equal(ignored, Map(fields).OriginalPositionFor(0, 1)!.Ignored);

    [Fact]
    public void Each_section_of_an_index_map_reads_its_own_list()
    {
        const string json = """
        {"version":3,"sections":[
          {"offset":{"line":0,"column":0},"map":{"version":3,"sources":["vendor/a.js"],"ignoreList":[0],"names":[],"mappings":"AAAA"}},
          {"offset":{"line":1,"column":0},"map":{"version":3,"sources":["src/b.ts"],"names":[],"mappings":"AAAA"}}
        ]}
        """;
        var map = SourceMap.Parse(Encoding.UTF8.GetBytes(json))!;
        Assert.True(map.OriginalPositionFor(0, 0)!.Ignored);
        Assert.False(map.OriginalPositionFor(1, 0)!.Ignored);
    }
}
