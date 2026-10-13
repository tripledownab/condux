using System.Buffers.Binary;
using System.Text;
using Condux.Core.DebugFiles;
using Xunit;
using static Condux.Tests.Shared.ElfFiles;

namespace Condux.Core.Tests;

/// <summary>What an uploaded native debug file must prove (a little-endian ELF file with a GNU build ID
/// and DWARF line and info sections), and how its build ID becomes the <c>debug_id</c> an event carries.
/// The files are built here byte by byte, so each test names exactly the one property it breaks.</summary>
public sealed class ElfDebugFileTests
{
    // Taken from a real obfuscated Flutter build: the build ID in its symbols file, and the debug_id the
    // crash from that build carried.
    private const string FlutterBuildId = "7a3ef9306827da183fa36f07eecef2fb";
    private const string FlutterDebugId = "30f93e7a-2768-18da-3fa3-6f07eecef2fb";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_symbols_file_yields_its_build_id_and_the_debug_id_an_event_carries(bool wide)
    {
        var (build, rejection) = ElfDebugFile.ToBuild(SymbolsFile(FlutterBuildId, wide));

        Assert.Null(rejection);
        Assert.Equal(new ElfBuild(FlutterBuildId, FlutterDebugId), build);
    }

    [Fact]
    public void A_stripped_binary_with_the_same_build_id_is_refused()
    {
        // libapp.so carries the build ID too, and a header for each debug section kept as NOBITS. Storing it
        // would answer every lookup for that build with a file that resolves nothing.
        var file = Elf(true,
            BuildIdNote(FlutterBuildId),
            new Section(".debug_info", NoBits, [], 4096),
            new Section(".debug_line", NoBits, [], 4096));

        Assert.Equal(ElfRejection.NoDebugInfo, ElfDebugFile.ToBuild(file).Rejection);
    }

    [Fact]
    public void Line_tables_alone_are_not_enough()
    {
        var file = Elf(true, BuildIdNote(FlutterBuildId), new Section(".debug_line", Progbits, [1, 2, 3]));

        Assert.Equal(ElfRejection.NoDebugInfo, ElfDebugFile.ToBuild(file).Rejection);
    }

    [Fact]
    public void Debug_info_alone_is_not_enough()
    {
        var file = Elf(true, BuildIdNote(FlutterBuildId), Info());

        Assert.Equal(ElfRejection.NoDebugInfo, ElfDebugFile.ToBuild(file).Rejection);
    }

    [Fact]
    public void A_section_name_that_only_starts_with_a_debug_name_does_not_count()
    {
        var file = Elf(true, BuildIdNote(FlutterBuildId),
            new Section(".debug_infos", Progbits, [1]), new Section(".debug_lines", Progbits, [1]));

        Assert.Equal(ElfRejection.NoDebugInfo, ElfDebugFile.ToBuild(file).Rejection);
    }

    [Fact]
    public void A_file_without_a_build_id_is_refused()
    {
        var file = Elf(true, Info(), Line());

        Assert.Equal(ElfRejection.NoBuildId, ElfDebugFile.ToBuild(file).Rejection);
    }

    [Fact]
    public void The_build_id_is_found_after_another_note()
    {
        var abiTag = NoteBytes("GNU", 1, [0, 0, 0, 0, 3, 0, 0, 0, 2, 0, 0, 0, 0, 0, 0, 0]);
        var notes = new Section(".note", Note, [.. abiTag, .. NoteBytes("GNU", 3, Convert.FromHexString(FlutterBuildId))]);

        var (build, _) = ElfDebugFile.ToBuild(Elf(true, notes, Info(), Line()));

        Assert.Equal(FlutterBuildId, build?.BuildId);
    }

    [Fact]
    public void A_note_from_another_vendor_is_not_a_build_id()
    {
        // A four-byte name, so the size check passes and only the name itself can refuse it.
        var notes = new Section(".note", Note, NoteBytes("XYZ", 3, Convert.FromHexString(FlutterBuildId)));

        Assert.Equal(ElfRejection.NoBuildId, ElfDebugFile.ToBuild(Elf(true, notes, Info(), Line())).Rejection);
    }

    [Fact]
    public void A_note_claiming_more_bytes_than_its_section_holds_is_not_read()
    {
        var note = NoteBytes("GNU", 3, Convert.FromHexString(FlutterBuildId));
        // 32 is within the size a build ID may have, so only the section's end can refuse it.
        BinaryPrimitives.WriteUInt32LittleEndian(note.AsSpan(4), 32);

        var file = Elf(true, new Section(".note", Note, note), Info(), Line());

        Assert.Equal(ElfRejection.NoBuildId, ElfDebugFile.ToBuild(file).Rejection);
    }

    [Fact]
    public void Note_headers_pointing_at_one_region_are_read_once_in_total_not_once_each()
    {
        // Five note headers aimed at one large note region, then the real build ID last. Reading every
        // header in full would find it; the scan stops once the notes read exceed the file's own length.
        var filler = new Section(".note.filler", Note, NoteBytes("GNU", 1, new byte[1024]));
        var aliases = Enumerable.Range(0, 5).Select(i => new Section($".note.alias{i}", Note, [0, 0, 0, 0])).ToArray();
        var file = Elf(true, [filler, .. aliases, BuildIdNote(FlutterBuildId), Info(), Line()]);
        var table = (int)BinaryPrimitives.ReadInt64LittleEndian(file.AsSpan(0x28));
        var fillerOffset = BinaryPrimitives.ReadInt64LittleEndian(file.AsSpan(table + 64 + 24));
        var fillerSize = BinaryPrimitives.ReadInt64LittleEndian(file.AsSpan(table + 64 + 32));
        for (var alias = 2; alias <= 6; alias++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(file.AsSpan(table + alias * 64 + 24), fillerOffset);
            BinaryPrimitives.WriteInt64LittleEndian(file.AsSpan(table + alias * 64 + 32), fillerSize);
        }

        Assert.Equal(ElfRejection.NoBuildId, ElfDebugFile.ToBuild(file).Rejection);
    }

    [Theory]
    [InlineData("not an elf file at all")]
    [InlineData("")]
    public void Anything_else_is_not_elf(string text)
    {
        Assert.Equal(ElfRejection.NotElf, ElfDebugFile.ToBuild(Encoding.ASCII.GetBytes(text)).Rejection);
    }

    [Fact]
    public void A_big_endian_file_is_unsupported()
    {
        var file = SymbolsFile(FlutterBuildId);
        file[5] = 2;

        Assert.Equal(ElfRejection.Unsupported, ElfDebugFile.ToBuild(file).Rejection);
    }

    [Fact]
    public void A_section_reaching_past_the_end_is_malformed()
    {
        var file = SymbolsFile(FlutterBuildId);
        var table = (int)BinaryPrimitives.ReadInt64LittleEndian(file.AsSpan(0x28));
        // Section 1's size field, pointed far past the end of the file.
        BinaryPrimitives.WriteInt64LittleEndian(file.AsSpan(table + 64 + 32), long.MaxValue);

        Assert.Equal(ElfRejection.Malformed, ElfDebugFile.ToBuild(file).Rejection);
    }

    [Fact]
    public void A_section_table_beyond_the_file_is_malformed()
    {
        var file = SymbolsFile(FlutterBuildId);
        BinaryPrimitives.WriteInt64LittleEndian(file.AsSpan(0x28), long.MaxValue - 16);

        Assert.Equal(ElfRejection.Malformed, ElfDebugFile.ToBuild(file).Rejection);
    }

    [Fact]
    public void A_header_cut_short_is_malformed()
    {
        Assert.Equal(ElfRejection.Malformed, ElfDebugFile.ToBuild(SymbolsFile(FlutterBuildId).AsSpan(0, 40)).Rejection);
    }

    [Fact]
    public void A_twenty_byte_build_id_keeps_all_of_it_and_takes_its_debug_id_from_the_first_sixteen()
    {
        const string sha1BuildId = "7a3ef9306827da183fa36f07eecef2fb01020304";
        var (build, _) = ElfDebugFile.ToBuild(Elf(true, BuildIdNote(sha1BuildId), Info(), Line()));

        Assert.Equal(sha1BuildId, build?.BuildId);
        Assert.Equal(FlutterDebugId, build?.DebugId);
    }

    [Fact]
    public void A_short_build_id_is_zero_padded_into_its_debug_id()
    {
        Assert.Equal("30f93e7a-0000-0000-0000-000000000000", ElfDebugFile.ToDebugId("7a3ef930"));
    }
}
