namespace Condux.Core.SourceMaps;

/// <summary>
/// An original source position resolved from a generated (line, column) through a source map (ADR-0028).
/// <see cref="Line"/> and <see cref="Column"/> are 0-based, matching the source-map spec (callers convert
/// to/from 1-based stack-frame numbers). <see cref="Name"/> is the original symbol name and
/// <see cref="SourceLine"/> the original source line, each null when the map does not carry it.
/// </summary>
public sealed record OriginalPosition(
    string Source, int Line, int Column, string? Name, string? SourceLine);
