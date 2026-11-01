using System.Text.Json;

namespace Condux.Core.SourceMaps;

/// <summary>
/// A cheap sanity check that an uploaded blob is actually a JSON source map (ADR-0028), so the store
/// rejects mistakes and abuse early rather than accepting arbitrary bytes as a "source map". Follows the
/// Source Map v3 spec: a JSON object with <c>version: 3</c> and either <c>mappings</c> (a plain map) or
/// <c>sections</c> (an index map), and mappings the decoder will accept. Not a
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
            if (SourceMapJson.Scan(content) is not { IsVersion3: true } root)
            {
                return false;
            }

            // The one thing here that is not a shape check, and the reason is that the caller is not asking
            // a question about shape: mappings the decoder will refuse make a map that uploads, stores and
            // then silently symbolicates nothing. The decoder owns the rule, and as in the decoder, a
            // "sections" field makes it an index map whatever else it holds. An index map with no usable
            // section is accepted, though a read finds nothing in it: Turbopack writes empty index maps for
            // its own build transforms, and the upload CLI sends every map it finds, so refusing them failed
            // the CLI on any such build. The strings a read would decode are confirmed to be text, so a map
            // accepted here is one a read can use.
            if (root.HasSections)
            {
                if (!SourceMapSections.IsDecodable(content, root.Sections))
                {
                    return false;
                }
                foreach (var section in root.Sections!)
                {
                    SourceMapJson.CheckStrings(content, section.Map);
                }
                return true;
            }

            if (root.Mappings is not { } mappings || !SourceMapMappings.Fits(SourceMapJson.MeasureMappings(content, mappings)))
            {
                return false;
            }
            SourceMapJson.CheckStrings(content, root);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
