using System.Buffers.Binary;

namespace Condux.Core.DebugFiles;

// Reads what an uploaded native debug file must prove before it is stored: that it is a little-endian ELF
// file, which build it belongs to (its GNU build ID note), and that it carries the DWARF a symbolicator
// needs. The bytes come from whoever holds a release token, so every offset is checked against the length
// before it is read, and the work stays proportional to the file: no byte is scanned once per section.

/// <summary>Why an upload is not a usable debug file.</summary>
public enum ElfRejection
{
    NotElf,
    Unsupported,
    Malformed,
    NoBuildId,
    NoDebugInfo,
}

/// <summary>A debug file's identity: its GNU build ID in lowercase hex, and the <c>debug_id</c> an event
/// carries for the same build.</summary>
public sealed record ElfBuild(string BuildId, string DebugId);

public static class ElfDebugFile
{
    private const uint SectionNote = 7;
    private const uint NoteGnuBuildId = 3;

    /// <summary>The build a file belongs to, or the reason it cannot be used. A stripped binary such as
    /// <c>libapp.so</c> carries the same build ID as its symbols but no DWARF, so it is refused rather than
    /// stored as if it could resolve a frame.</summary>
    public static (ElfBuild? Build, ElfRejection? Rejection) ToBuild(ReadOnlySpan<byte> file)
    {
        if (file.Length < 16 || file[0] != 0x7F || file[1] != (byte)'E' || file[2] != (byte)'L' || file[3] != (byte)'F')
        {
            return (null, ElfRejection.NotElf);
        }

        // Class 1 is 32-bit, 2 is 64-bit. Data 1 is little-endian, which every Android ABI is.
        var wide = file[4] == 2;
        if (file[4] is not (1 or 2) || file[5] != 1)
        {
            return (null, ElfRejection.Unsupported);
        }

        var reader = new ElfReader(file, wide);
        if (!reader.TryReadSections(out var sections, out var names))
        {
            return (null, ElfRejection.Malformed);
        }

        string? buildId = null;
        // Section headers may all point at one region, so the notes read are capped at the file's own length
        // in total rather than per section.
        long noteBudget = file.Length;
        var hasInfo = false;
        var hasLine = false;
        foreach (var section in sections)
        {
            // A stripped file can keep a debug section's header as NOBITS, with a size and no bytes.
            var hasBytes = section.Type != ElfReader.SectionNoBits && section.Size > 0;
            hasInfo |= hasBytes && reader.NameIs(names, section.NameOffset, ".debug_info"u8);
            hasLine |= hasBytes && reader.NameIs(names, section.NameOffset, ".debug_line"u8);
            if (section.Type == SectionNote && buildId is null)
            {
                noteBudget -= section.Size;
                if (noteBudget >= 0)
                {
                    buildId = ReadBuildIdNote(reader.Slice(section));
                }
            }
        }

        if (buildId is null)
        {
            return (null, ElfRejection.NoBuildId);
        }

        return hasInfo && hasLine ? (new ElfBuild(buildId, ToDebugId(buildId)), null) : (null, ElfRejection.NoDebugInfo);
    }

    /// <summary>The <c>debug_id</c> for an ELF build ID, by Sentry's own server-side rule (symbolic's
    /// <c>compute_debug_id</c> in <c>symbolic-debuginfo/src/elf.rs</c>): the first 16 bytes, zero-padded
    /// when shorter, read as a GUID whose first three groups are little-endian. sentry-dart computes the
    /// same for a 16-byte ID, which is what a Dart build has; it pads a shorter one differently.</summary>
    public static string ToDebugId(string buildId)
    {
        var bytes = new byte[16];
        var raw = Convert.FromHexString(buildId);
        raw.AsSpan(0, Math.Min(16, raw.Length)).CopyTo(bytes);
        return new Guid(bytes).ToString();
    }

    /// <summary>An event's <c>debug_id</c> in the form <see cref="ToDebugId"/> writes, or null when it is not
    /// one. Senders vary the spelling (case, no dashes, braces) and may append an age after a dash, as
    /// Sentry's own format allows, so a plain string match would miss files that are there.</summary>
    public static string? ToStoredDebugId(string debugId)
    {
        if (Guid.TryParse(debugId, out var whole))
        {
            return whole.ToString();
        }
        var age = debugId.LastIndexOf('-');
        return age > 0 && Guid.TryParse(debugId.AsSpan(0, age), out var aged) ? aged.ToString() : null;
    }

    // Notes are 4-byte aligned records of (namesz, descsz, type, name, desc). Only the GNU build ID is read.
    private static string? ReadBuildIdNote(ReadOnlySpan<byte> notes)
    {
        var at = 0;
        while (at + 12 <= notes.Length)
        {
            var nameSize = BinaryPrimitives.ReadUInt32LittleEndian(notes[(at)..]);
            var descSize = BinaryPrimitives.ReadUInt32LittleEndian(notes[(at + 4)..]);
            var type = BinaryPrimitives.ReadUInt32LittleEndian(notes[(at + 8)..]);
            var name = (long)at + 12;
            var desc = name + Align4(nameSize);
            var next = desc + Align4(descSize);
            if (next > notes.Length)
            {
                return null;
            }
            if (type == NoteGnuBuildId && nameSize == 4 && notes.Slice((int)name, 4).SequenceEqual("GNU\0"u8)
                && descSize is > 0 and <= 64)
            {
                return Convert.ToHexStringLower(notes.Slice((int)desc, (int)descSize));
            }
            at = (int)next;
        }

        return null;
    }

    private static long Align4(uint size) => ((long)size + 3) & ~3L;
}
