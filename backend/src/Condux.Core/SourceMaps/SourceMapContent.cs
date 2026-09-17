using System.Text.Json;

namespace Condux.Core.SourceMaps;

/// <summary>
/// A cheap sanity check that an uploaded blob is actually a JSON source map (ADR-0028), so the store
/// rejects mistakes and abuse early rather than accepting arbitrary bytes as a "source map". Follows the
/// Source Map v3 spec: a JSON object with <c>version: 3</c> and either <c>mappings</c> (a plain map) or
/// <c>sections</c> (an index map), and for a plain map a mappings string the decoder will accept. Not a
/// full validation, just enough to reject what could never be read back.
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

            if (root.TryGetProperty("mappings", out var mappings) && mappings.ValueKind == JsonValueKind.String)
            {
                // The one thing here that is not a shape check, and the reason is that the caller is not
                // asking a question about shape: a mappings string the decoder will refuse is a map that
                // uploads, stores and then silently symbolicates nothing. The decoder owns the rule.
                return SourceMapMappings.IsDecodable(mappings.GetString()!);
            }

            return root.TryGetProperty("sections", out var sections) && sections.ValueKind == JsonValueKind.Array;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
