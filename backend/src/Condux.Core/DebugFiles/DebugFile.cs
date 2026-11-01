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

/// <summary>A debug file's identity: the build it belongs to, as the file names it (an ELF GNU build ID, or a
/// Mach-O <c>LC_UUID</c>, in lowercase hex), and the <c>debug_id</c> an event carries for the same build.
/// </summary>
public sealed record DebugBuild(string BuildId, string DebugId);

/// <summary>One build an uploaded file holds, and where its bytes lie in the upload: the whole file for ELF
/// and for a thin Mach-O file, one architecture's slice of a universal one. <see cref="Architecture"/> is
/// named for Mach-O, where one upload can hold several, and null for ELF, which holds one.</summary>
public sealed record DebugFileSlice(DebugBuild Build, string? Architecture, int Offset, int Length);
