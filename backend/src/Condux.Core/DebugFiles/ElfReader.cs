using System.Buffers.Binary;

namespace Condux.Core.DebugFiles;

// Reads an ELF file's section header table, little-endian, 32- or 64-bit. It returns a section only after
// checking that its bytes lie inside the file, so a caller may slice any section it is handed. Sections of
// type NOBITS carry a size but no bytes (a stripped .text in a symbols file is one), so they are kept for
// their name and type but never bounds-checked or sliced.

internal readonly record struct ElfSection(uint NameOffset, uint Type, long Offset, long Size);

internal readonly ref struct ElfReader(ReadOnlySpan<byte> file, bool wide)
{
    public const uint SectionNoBits = 8;

    // The table index that means "the real value lives elsewhere": extended numbering, which no toolchain
    // uses for a file this small, so it is refused rather than followed.
    private const ushort ExtendedIndex = 0xFFFF;

    private readonly ReadOnlySpan<byte> _file = file;

    public bool TryReadSections(out ElfSection[] sections, out ElfSection names)
    {
        sections = [];
        names = default;
        var headerSize = wide ? 64 : 52;
        if (_file.Length < headerSize)
        {
            return false;
        }

        var tableOffset = wide ? BinaryPrimitives.ReadInt64LittleEndian(_file[0x28..]) : ReadU32(0x20);
        var entrySize = ReadU16(wide ? 0x3A : 0x2E);
        var count = ReadU16(wide ? 0x3C : 0x30);
        var namesIndex = ReadU16(wide ? 0x3E : 0x32);
        if (count == 0 || namesIndex == ExtendedIndex || namesIndex >= count
            || entrySize < (wide ? 64 : 40) || tableOffset < 0
            || tableOffset > _file.Length - (long)count * entrySize)
        {
            return false;
        }

        var read = new ElfSection[count];
        for (var i = 0; i < count; i++)
        {
            var at = (int)(tableOffset + (long)i * entrySize);
            var section = wide
                ? new ElfSection(ReadU32(at), ReadU32(at + 4),
                    BinaryPrimitives.ReadInt64LittleEndian(_file[(at + 24)..]),
                    BinaryPrimitives.ReadInt64LittleEndian(_file[(at + 32)..]))
                : new ElfSection(ReadU32(at), ReadU32(at + 4), ReadU32(at + 16), ReadU32(at + 20));
            if (section.Type != SectionNoBits && !IsInside(section))
            {
                return false;
            }
            read[i] = section;
        }

        if (read[namesIndex].Type == SectionNoBits)
        {
            return false;
        }

        sections = read;
        names = read[namesIndex];
        return true;
    }

    /// <summary>Whether a section's name, read from the section name table, is exactly <paramref name="expected"/>.</summary>
    public bool NameIs(ElfSection names, uint nameOffset, ReadOnlySpan<byte> expected)
    {
        var table = Slice(names);
        if (nameOffset >= table.Length)
        {
            return false;
        }

        // Compared over the expected name and its terminator only: scanning for the terminator instead would
        // let a table with none make every section read the whole table.
        var rest = table[(int)nameOffset..];
        return rest.Length > expected.Length && rest[..expected.Length].SequenceEqual(expected)
            && rest[expected.Length] == 0;
    }

    /// <summary>A section's bytes. Only valid for a section this reader returned that is not NOBITS.</summary>
    public ReadOnlySpan<byte> Slice(ElfSection section) => _file.Slice((int)section.Offset, (int)section.Size);

    private bool IsInside(ElfSection section) =>
        section.Offset >= 0 && section.Size >= 0 && section.Offset <= _file.Length - section.Size;

    private uint ReadU32(int at) => BinaryPrimitives.ReadUInt32LittleEndian(_file[at..]);

    private ushort ReadU16(int at) => BinaryPrimitives.ReadUInt16LittleEndian(_file[at..]);
}
