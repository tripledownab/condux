namespace Condux.Core.DebugFiles;

/// <summary>
/// An uploaded native debug file, indexed by the build it belongs to. The file lives in object storage under
/// <see cref="ObjectKey"/>; this record is the Postgres index. <see cref="BuildId"/> is read from the file
/// itself and <see cref="DebugId"/> is the form an event's debug image names the same build by.
/// <see cref="Sha256"/> is the name the symbolizer holds the file under (<see cref="DebugFileHashes"/>);
/// null for a file uploaded before it was recorded, until a read fills it in.
/// </summary>
public sealed record DebugFile(
    Guid Id,
    long ProjectId,
    string BuildId,
    string DebugId,
    string ObjectKey,
    long ByteSize,
    DateTimeOffset CreatedAt,
    string? Sha256);
