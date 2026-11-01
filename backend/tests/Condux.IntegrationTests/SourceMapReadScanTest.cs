using Condux.IntegrationTests.Fixtures;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// Source maps are uploaded by anyone holding a release token and read again on every view of an issue,
/// so they are read token by token (SourceMapJson) and never built into a JSON document, whose index holds
/// an entry for every value whatever field it sits in, nor into a JsonNode tree, which holds an object for
/// each. A document's index is rented from a pool, so an allocation count cannot see one being built;
/// this scan is what keeps one from coming back.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SourceMapReadScanTest
{
    [Fact]
    public void No_source_map_code_builds_a_json_document()
    {
        var lines = RepoFiles.ReadSourceLines()
            .Where(line => line.File.StartsWith(Path.Combine("Condux.Core", "SourceMaps"), StringComparison.Ordinal))
            .ToList();

        var offenders = lines
            .Where(line => line.Text.Contains("JsonDocument", StringComparison.Ordinal)
                || line.Text.Contains("JsonElement", StringComparison.Ordinal)
                || line.Text.Contains("JsonNode", StringComparison.Ordinal))
            .Select(line => $"{line.File}:{line.Number}")
            .ToList();

        Assert.True(lines.Select(line => line.File).Distinct().Count() > 5, "found too few source map files to scan");
        Assert.True(offenders.Count == 0, $"read the map with SourceMapJson instead: {string.Join(", ", offenders)}");
    }
}
