namespace Condux.Core.SourceMaps;

/// <summary>
/// An uploaded client source map, indexed for read-time symbolication (ADR-0028). The map bytes live in
/// object storage under <see cref="ObjectKey"/>; this record is the Postgres index. Resolved by
/// <see cref="DebugId"/> first, then the (<see cref="Release"/>, <see cref="Dist"/>, <see cref="Filename"/>)
/// fallback for a plain CI upload.
/// </summary>
public sealed record SourceMapArtifact(
    Guid Id,
    long ProjectId,
    string Release,
    string? Dist,
    string? DebugId,
    string Filename,
    string ObjectKey,
    long ByteSize,
    DateTimeOffset CreatedAt);
