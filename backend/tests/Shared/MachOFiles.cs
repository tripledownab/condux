using System.Buffers.Binary;
using System.Text;

namespace Condux.Tests.Shared;

// Builds Mach-O files byte by byte for the tests of native debug file handling, as ElfFiles does for ELF:
// the parser's unit tests and the upload endpoint's integration tests both need files with exactly one
// property changed. 64-bit little-endian thin files, and universal files wrapping them.
public static class MachOFiles
{
    public const uint Arm64 = 0x0100000C;
    public const uint X86_64 = 0x01000007;

    /// <summary>A dSYM's DWARF file: a UUID and a __DWARF segment with info and line tables.</summary>
    public static byte[] Dsym(string uuid, uint cpu = Arm64) =>
        Thin(cpu, UuidCommand(uuid), Segment("__DWARF", ("__debug_info", 4), ("__debug_line", 4)));

    /// <summary>A thin file holding the given load commands, with each section's bytes appended after them
    /// and its offset pointed there; a section of size zero gets no bytes and offset zero.</summary>
    public static byte[] Thin(uint cpu, params byte[][] commands)
    {
        var commandBytes = commands.SelectMany(c => c).ToArray();
        var file = new List<byte>(new byte[32]);
        file.AddRange(commandBytes);
        var header = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0xFEEDFACF);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), cpu);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), 0xA); // MH_DSYM
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), (uint)commands.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), (uint)commandBytes.Length);
        var bytes = file.ToArray();
        header.CopyTo(bytes, 0);
        return PlaceSectionBytes(bytes);
    }

    public static byte[] UuidCommand(string uuid)
    {
        var command = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(command, 0x1B);
        BinaryPrimitives.WriteUInt32LittleEndian(command.AsSpan(4), 24);
        Guid.Parse(uuid).TryWriteBytes(command.AsSpan(8), bigEndian: true, out _);
        return command;
    }

    /// <summary>A 64-bit segment command; each section is (name, size). Offsets are filled in by
    /// <see cref="Thin"/>, which marks them for placement here with the sentinel offset 1.</summary>
    public static byte[] Segment(string name, params (string Name, ulong Size)[] sections)
    {
        var command = new byte[72 + sections.Length * 80];
        BinaryPrimitives.WriteUInt32LittleEndian(command, 0x19);
        BinaryPrimitives.WriteUInt32LittleEndian(command.AsSpan(4), (uint)command.Length);
        Encoding.ASCII.GetBytes(name).CopyTo(command, 8);
        BinaryPrimitives.WriteUInt32LittleEndian(command.AsSpan(64), (uint)sections.Length);
        for (var i = 0; i < sections.Length; i++)
        {
            var at = 72 + i * 80;
            Encoding.ASCII.GetBytes(sections[i].Name).CopyTo(command, at);
            Encoding.ASCII.GetBytes(name).CopyTo(command, at + 16);
            BinaryPrimitives.WriteUInt64LittleEndian(command.AsSpan(at + 40), sections[i].Size);
            BinaryPrimitives.WriteUInt32LittleEndian(command.AsSpan(at + 48), sections[i].Size > 0 ? 1u : 0u);
        }
        return command;
    }

    /// <summary>A universal file holding the thin files given, each at a 16-byte aligned offset.</summary>
    public static byte[] Universal(bool wide, params byte[][] slices)
    {
        var entry = wide ? 32 : 20;
        var bytes = new List<byte>(new byte[8 + slices.Length * entry]);
        var header = bytes.ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(header, wide ? 0xCAFEBABF : 0xCAFEBABE);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)slices.Length);
        for (var i = 0; i < slices.Length; i++)
        {
            while (bytes.Count % 16 != 0)
            {
                bytes.Add(0);
            }
            var at = 8 + i * entry;
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(at), BinaryPrimitives.ReadUInt32LittleEndian(slices[i].AsSpan(4)));
            if (wide)
            {
                BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(at + 8), (ulong)bytes.Count);
                BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(at + 16), (ulong)slices[i].Length);
            }
            else
            {
                BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(at + 8), (uint)bytes.Count);
                BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(at + 12), (uint)slices[i].Length);
            }
            bytes.AddRange(slices[i]);
        }
        var result = bytes.ToArray();
        header.CopyTo(result, 0);
        return result;
    }

    // Appends each marked section's bytes after the load commands and points its offset there.
    private static byte[] PlaceSectionBytes(byte[] file)
    {
        var output = new List<byte>(file);
        var commandsEnd = 32 + (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(20));
        for (var at = 32; at < commandsEnd;)
        {
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(at + 4));
            if (BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(at)) == 0x19)
            {
                var sections = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(at + 64));
                for (var i = 0; i < sections; i++)
                {
                    var section = at + 72 + i * 80;
                    if (BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(section + 48)) != 1)
                    {
                        continue;
                    }
                    var length = (int)BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(section + 40));
                    var bytes = output.ToArray();
                    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(section + 48), (uint)bytes.Length);
                    output = [.. bytes, .. Enumerable.Repeat((byte)0xDB, length)];
                }
            }
            at += size;
        }
        return output.ToArray();
    }
}
