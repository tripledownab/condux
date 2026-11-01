namespace Condux.Core.SourceMaps;

/// <summary>
/// Where the fields of one map object lie in the document <see cref="SourceMapJson"/> scanned, so that only
/// the maps a read would decode are measured, checked or have their strings read, and the rest cost
/// nothing beyond the scan.
/// </summary>
internal struct SourceMapFields
{
    /// <summary>Whether "version" is the number 3, as the upload check requires. Read on the root only.</summary>
    public bool IsVersion3;

    /// <summary>Where each of these fields' values lies, or null when the map does not have the field; for
    /// "mappings", also null when it is not a string.</summary>
    public JsonRange? Mappings, Sources, SourcesContent, Names, IgnoreList, XGoogleIgnoreList;

    /// <summary>Whether the map has a "sections" field at all, which makes it an index map whatever else it
    /// holds (ECMA-426 ParseSourceMap). Read on the root only.</summary>
    public bool HasSections;

    /// <summary>The sections a decode would read (<see cref="SourceMapSections.Planner"/>), or null when
    /// "sections" is not an array or the index map cannot be decoded at all.</summary>
    public List<PlannedSection>? Sections;
}

/// <summary>A value's bytes in the scanned document, quotes and brackets included.</summary>
internal readonly record struct JsonRange(int Start, int Length);

/// <summary>A section an index map's decode reads: its offset and its map.</summary>
internal readonly record struct PlannedSection(int Line, int Column, SourceMapFields Map);
