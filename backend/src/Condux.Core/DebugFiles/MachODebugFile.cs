using System.Buffers.Binary;

namespace Condux.Core.DebugFiles;

// Reads what an uploaded Mach-O debug file (the DWARF file inside a dSYM) must prove: which builds it holds,
// one per architecture, each named by its LC_UUID, which is the debug_id an Apple crash carries for that
// image; and that each carries the DWARF a symbolicator needs. A universal file holds several thin files
// back to back. Each is returned as a slice to store on its own, because llvm-symbolizer refuses a
// universal file unless told the architecture. The bytes come from whoever holds a release token, so every
// offset is checked against the length before it is read. A thin file's load commands are read once, and a
// universal file's at most once per architecture, of which there are few.

/// <summary>Why an upload is not a usable Mach-O debug file.</summary>
public enum MachORejection
{
    NotMachO,
    Unsupported,
    Malformed,
    NoUuid,
    NoDebugInfo,
}

public static class MachODebugFile
{
    private const uint UniversalMagic = 0xCAFEBABE;
    private const uint UniversalMagic64 = 0xCAFEBABF;
    private const uint Magic64 = 0xFEEDFACF;
    private const uint Magic32 = 0xFEEDFACE;
    private const uint CommandUuid = 0x1B;
    private const uint CommandSegment64 = 0x19;
    private const int HeaderSize = 32;
    private const int SegmentSize = 72;
    private const int SectionSize = 80;

    // A universal file holds a handful of architectures. A Java class file opens with the same four bytes
    // and then its version where the count would be, so a large count means it is not Mach-O at all.
    private const uint MaxArchitectures = 20;

    /// <summary>Whether the file opens like Mach-O, thin or universal, in either byte order.</summary>
    public static bool LooksLikeMachO(ReadOnlySpan<byte> file) =>
        file.Length >= 4 && (BinaryPrimitives.ReadUInt32BigEndian(file) is UniversalMagic or UniversalMagic64
            or Magic64 or Magic32
            || BinaryPrimitives.ReadUInt32LittleEndian(file) is Magic64 or Magic32);

    /// <summary>Every build the file holds, or the reason it cannot be used. A binary that shipped (the
    /// app's <c>App.framework/App</c>) carries the same UUID as its dSYM but no DWARF, so it is refused
    /// rather than stored as if it could resolve a frame.</summary>
    public static (IReadOnlyList<DebugFileSlice>? Slices, MachORejection? Rejection) ToSlices(ReadOnlySpan<byte> file)
    {
        if (file.Length < 8)
        {
            return (null, MachORejection.NotMachO);
        }
        var magic = BinaryPrimitives.ReadUInt32BigEndian(file);
        if (magic is UniversalMagic or UniversalMagic64)
        {
            return ReadUniversal(file, magic == UniversalMagic64);
        }

        var (build, architecture, rejection) = ReadThin(file);
        return build is null ? (null, rejection) : ([new DebugFileSlice(build, architecture, 0, file.Length)], null);
    }

    /// <summary>The <c>debug_id</c> for a Mach-O UUID: the UUID itself, in the byte order it is stored in,
    /// with no swapping. That differs from ELF (<see cref="ElfDebugFile.ToDebugId"/>), and is measured:
    /// sentry-cocoa reported an image's <c>debug_id</c> exactly as <c>dwarfdump --uuid</c> prints its
    /// <c>LC_UUID</c>.</summary>
    public static string ToDebugId(ReadOnlySpan<byte> uuid) => new Guid(uuid, bigEndian: true).ToString();

    private static (IReadOnlyList<DebugFileSlice>?, MachORejection?) ReadUniversal(ReadOnlySpan<byte> file, bool wide)
    {
        var count = BinaryPrimitives.ReadUInt32BigEndian(file[4..]);
        if (count is 0 or > MaxArchitectures)
        {
            return (null, MachORejection.NotMachO);
        }
        var entrySize = wide ? 32 : 20;
        var tableEnd = 8L + count * entrySize;
        if (tableEnd > file.Length)
        {
            return (null, MachORejection.Malformed);
        }

        var slices = new List<DebugFileSlice>((int)count);
        // Each slice must start where the previous one ended or later. Real universal files are laid out so,
        // and slices sharing bytes would let one upload be stored many times over against the org's quota.
        var nextFree = (ulong)tableEnd;
        for (var i = 0; i < count; i++)
        {
            var entry = file.Slice(8 + i * entrySize, entrySize);
            var offset = wide ? BinaryPrimitives.ReadUInt64BigEndian(entry[8..]) : BinaryPrimitives.ReadUInt32BigEndian(entry[8..]);
            var size = wide ? BinaryPrimitives.ReadUInt64BigEndian(entry[16..]) : BinaryPrimitives.ReadUInt32BigEndian(entry[12..]);
            if (offset < nextFree || offset > (ulong)file.Length || size > (ulong)file.Length - offset)
            {
                return (null, MachORejection.Malformed);
            }
            nextFree = offset + size;
            var (build, architecture, rejection) = ReadThin(file.Slice((int)offset, (int)size));
            if (build is null)
            {
                return (null, rejection);
            }
            slices.Add(new DebugFileSlice(build, architecture, (int)offset, (int)size));
        }

        // Each slice is stored under its own build, so two slices claiming one UUID would overwrite each other.
        return slices.DistinctBy(slice => slice.Build.BuildId).Count() == slices.Count
            ? (slices, null)
            : (null, MachORejection.Malformed);
    }

    private static (DebugBuild?, string?, MachORejection?) ReadThin(ReadOnlySpan<byte> file)
    {
        if (file.Length < HeaderSize)
        {
            return (null, null, MachORejection.NotMachO);
        }
        // Only 64-bit little-endian is what Apple's current platforms build; a 32-bit or big-endian file is
        // recognised so the caller is told it is unsupported rather than that it is not Mach-O.
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(file);
        if (magic != Magic64)
        {
            var known = magic == Magic32 || BinaryPrimitives.ReadUInt32BigEndian(file) is Magic64 or Magic32;
            return (null, null, known ? MachORejection.Unsupported : MachORejection.NotMachO);
        }

        var cpuType = BinaryPrimitives.ReadUInt32LittleEndian(file[4..]);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(file[16..]);
        var commandsSize = BinaryPrimitives.ReadUInt32LittleEndian(file[20..]);
        if (commandsSize > file.Length - HeaderSize)
        {
            return (null, null, MachORejection.Malformed);
        }

        var commands = file.Slice(HeaderSize, (int)commandsSize);
        byte[]? uuid = null;
        var hasInfo = false;
        var hasLine = false;
        var at = 0;
        // Every command is at least 8 bytes and must fit, so the loop ends within the commands region
        // whatever count the header claims.
        for (var i = 0u; i < count; i++)
        {
            if (at > commands.Length - 8)
            {
                return (null, null, MachORejection.Malformed);
            }
            var command = BinaryPrimitives.ReadUInt32LittleEndian(commands[at..]);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(commands[(at + 4)..]);
            if (size < 8 || size > commands.Length - at)
            {
                return (null, null, MachORejection.Malformed);
            }
            var body = commands.Slice(at, (int)size);
            if (command == CommandUuid)
            {
                if (body.Length < 24)
                {
                    return (null, null, MachORejection.Malformed);
                }
                uuid ??= body.Slice(8, 16).ToArray();
            }
            else if (command == CommandSegment64 && !ReadDwarfSections(body, file.Length, ref hasInfo, ref hasLine))
            {
                return (null, null, MachORejection.Malformed);
            }
            at += (int)size;
        }

        if (uuid is null)
        {
            return (null, null, MachORejection.NoUuid);
        }
        return hasInfo && hasLine
            ? (new DebugBuild(Convert.ToHexStringLower(uuid), ToDebugId(uuid)), ToArchitecture(cpuType), null)
            : (null, null, MachORejection.NoDebugInfo);
    }

    // A dSYM keeps its DWARF in the __DWARF segment. A section counts only when its bytes are in the file:
    // a size with no file offset is a header kept for a section that is not there.
    private static bool ReadDwarfSections(ReadOnlySpan<byte> segment, int fileLength, ref bool hasInfo, ref bool hasLine)
    {
        if (segment.Length < SegmentSize)
        {
            return false;
        }
        var sections = BinaryPrimitives.ReadUInt32LittleEndian(segment[64..]);
        if (sections > (segment.Length - SegmentSize) / SectionSize)
        {
            return false;
        }
        if (!NameIs(segment.Slice(8, 16), "__DWARF"u8))
        {
            return true;
        }

        for (var i = 0; i < sections; i++)
        {
            var section = segment.Slice(SegmentSize + i * SectionSize, SectionSize);
            var size = BinaryPrimitives.ReadUInt64LittleEndian(section[40..]);
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(section[48..]);
            // The offset is checked before the subtraction: past the end it would wrap, and any size would pass.
            var present = size > 0 && offset > 0 && offset <= fileLength && size <= (ulong)(fileLength - offset);
            hasInfo |= present && NameIs(section[..16], "__debug_info"u8);
            hasLine |= present && NameIs(section[..16], "__debug_line"u8);
        }
        return true;
    }

    // A 16-byte name field, NUL padded unless the name fills it.
    private static bool NameIs(ReadOnlySpan<byte> field, ReadOnlySpan<byte> name)
    {
        var end = field.IndexOf((byte)0);
        return (end < 0 ? field : field[..end]).SequenceEqual(name);
    }

    private static string ToArchitecture(uint cpuType) => cpuType switch
    {
        0x0100000C => "arm64",
        0x01000007 => "x86_64",
        _ => $"cpu-{cpuType:x}",
    };
}
