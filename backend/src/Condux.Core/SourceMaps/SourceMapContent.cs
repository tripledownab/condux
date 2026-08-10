using System.Text.Json;

namespace Condux.Core.SourceMaps;

/// <summary>
/// A cheap sanity check that an uploaded blob is actually a JSON source map (ADR-0028), so the store
/// rejects mistakes and abuse early rather than accepting arbitrary bytes as a "source map". Follows the
/// Source Map v3 spec: a JSON object with <c>version: 3</c> and either <c>mappings</c> (a plain map) or
/// <c>sections</c> (an index map). Not a full validation, just enough to reject non-maps.
/// </summary>
public static class SourceMapContent
{
    public static bool LooksLikeSourceMap(byte[] content)
    {
        if (content.Length == 0)
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("version", out var version)
                || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var v) || v != 3)
            {
                return false;
            }

            return (root.TryGetProperty("mappings", out var mappings) && mappings.ValueKind == JsonValueKind.String)
                || (root.TryGetProperty("sections", out var sections) && sections.ValueKind == JsonValueKind.Array);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
