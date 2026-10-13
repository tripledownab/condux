using System.Buffers.Binary;
using System.Text;

namespace Condux.Tests.Shared;

// Builds ELF files byte by byte for the tests of native debug file handling: the parser's unit tests and
// the upload endpoint's integration tests both need files with exactly one property changed, so the builder
// is linked into both test projects rather than written twice. Little-endian, 32- or 64-bit.
public static class ElfFiles
{
    public const uint Progbits = 1;
    public const uint Note = 7;
    public const uint NoBits = 8;

    /// <summary>A symbols file as a split-debug-info build writes one: a build ID, the code stripped to
    /// NOBITS, and DWARF info and line tables.</summary>
    public static byte[] SymbolsFile(string buildId, bool wide = true) =>
        Elf(wide, BuildIdNote(buildId), new Section(".text", NoBits, [], 1 << 20), Info(), Line());

    public static Section Info() => new(".debug_info", Progbits, [1, 2, 3, 4]);

    public static Section Line() => new(".debug_line", Progbits, [5, 6, 7, 8]);

    public static Section BuildIdNote(string hex) =>
        new(".note.gnu.build-id", Note, NoteBytes("GNU", 3, Convert.FromHexString(hex)));

    public static byte[] NoteBytes(string name, uint type, byte[] desc)
    {
        var nameBytes = Encoding.ASCII.GetBytes(name + "\0");
        var note = new byte[12 + Pad(nameBytes.Length) + Pad(desc.Length)];
        BinaryPrimitives.WriteUInt32LittleEndian(note, (uint)nameBytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(note.AsSpan(4), (uint)desc.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(note.AsSpan(8), type);
        nameBytes.CopyTo(note, 12);
        desc.CopyTo(note, 12 + Pad(nameBytes.Length));
        return note;
    }

    public sealed record Section(string Name, uint Type, byte[] Data, long NoBitsSize = 0);

    // Header, then each section's bytes, then the name table, then the section header table, with the
    // null section first and the name table last, as a linker lays them out.
    public static byte[] Elf(bool wide, params Section[] sections)
    {
        var all = new List<Section>(sections);
        var names = new List<byte> { 0 };
        var nameOffsets = new List<int>();
        foreach (var name in all.Select(s => s.Name).Append(".shstrtab"))
        {
            nameOffsets.Add(names.Count);
            names.AddRange(Encoding.ASCII.GetBytes(name + "\0"));
        }
        all.Add(new Section(".shstrtab", 3, [.. names]));

        var headerSize = wide ? 64 : 52;
        var entrySize = wide ? 64 : 40;
        var body = new List<byte>(new byte[headerSize]);
        var offsets = new List<long>();
        foreach (var section in all)
        {
            while (body.Count % 4 != 0)
            {
                body.Add(0);
            }
            offsets.Add(body.Count);
            body.AddRange(section.Data);
        }
        while (body.Count % 8 != 0)
        {
            body.Add(0);
        }

        var tableOffset = body.Count;
        var file = new byte[tableOffset + (all.Count + 1) * entrySize];
        body.CopyTo(file);
        "\u007fELF"u8.CopyTo(file);
        file[4] = (byte)(wide ? 2 : 1);
        file[5] = 1;
        file[6] = 1;
        if (wide)
        {
            BinaryPrimitives.WriteInt64LittleEndian(file.AsSpan(0x28), tableOffset);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x20), (uint)tableOffset);
        }
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(wide ? 0x3A : 0x2E), (ushort)entrySize);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(wide ? 0x3C : 0x30), (ushort)(all.Count + 1));
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(wide ? 0x3E : 0x32), (ushort)all.Count);

        for (var i = 0; i < all.Count; i++)
        {
            var at = tableOffset + (i + 1) * entrySize;
            var section = all[i];
            var size = section.Type == NoBits ? section.NoBitsSize : section.Data.Length;
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(at), (uint)nameOffsets[i]);
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(at + 4), section.Type);
            if (wide)
            {
                BinaryPrimitives.WriteInt64LittleEndian(file.AsSpan(at + 24), offsets[i]);
                BinaryPrimitives.WriteInt64LittleEndian(file.AsSpan(at + 32), size);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(at + 16), (uint)offsets[i]);
                BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(at + 20), (uint)size);
            }
        }

        return file;
    }

    private static int Pad(int size) => (size + 3) & ~3;
}
