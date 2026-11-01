using System.Buffers.Binary;
using System.Text;
using Condux.Core.DebugFiles;
using Xunit;
using static Condux.Tests.Shared.MachOFiles;

namespace Condux.Core.Tests;

/// <summary>What an uploaded Mach-O debug file must prove (a 64-bit little-endian file with an LC_UUID and
/// DWARF info and line sections in its __DWARF segment), how a universal file splits into one build per
/// architecture, and how a UUID becomes the <c>debug_id</c> an Apple crash carries. Built byte by byte, so
/// each test names the one property it breaks.</summary>
public sealed class MachODebugFileTests
{
    // From a real obfuscated Flutter macOS build: the arm64 slice's LC_UUID as dwarfdump prints it, which is
    // exactly the debug_id sentry-cocoa sent for the app image in a crash from that build.
    private const string MeasuredUuid = "E191A89D-E2C2-30C4-BF83-1D9222717212";
    private const string OtherUuid = "C4905206-86BE-3B69-9564-66EEDAD3CAEB";

    [Fact]
    public void A_dsym_yields_its_uuid_as_the_build_and_the_debug_id_an_apple_crash_carries()
    {
        var (slices, rejection) = MachODebugFile.ToSlices(Dsym(MeasuredUuid));

        Assert.Null(rejection);
        Assert.NotNull(slices);
        var slice = Assert.Single(slices);
        Assert.Equal(new DebugBuild("e191a89de2c230c4bf831d9222717212", "e191a89d-e2c2-30c4-bf83-1d9222717212"), slice.Build);
        Assert.Equal("arm64", slice.Architecture);
        // The match the native pass makes: the event's spelling, normalised, finds the stored build.
        Assert.Equal(slice.Build.DebugId, DebugFileFormats.ToStoredDebugId(MeasuredUuid));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_universal_file_splits_into_one_build_per_architecture(bool wide)
    {
        var arm = Dsym(MeasuredUuid, Arm64);
        var intel = Dsym(OtherUuid, X86_64);
        var file = Universal(wide, intel, arm);

        var (slices, rejection) = MachODebugFile.ToSlices(file);

        Assert.Null(rejection);
        Assert.NotNull(slices);
        Assert.Equal(["x86_64", "arm64"], slices.Select(s => s.Architecture));
        Assert.Equal([OtherUuid, MeasuredUuid], slices.Select(s => s.Build.DebugId.ToUpperInvariant()));
        // Each slice is the thin file itself, which is what gets stored and handed to the symbolizer.
        Assert.Equal(intel, file.AsSpan(slices[0].Offset, slices[0].Length).ToArray());
        Assert.Equal(arm, file.AsSpan(slices[1].Offset, slices[1].Length).ToArray());
    }

    [Fact]
    public void The_binary_that_shipped_is_refused_for_having_no_dwarf()
    {
        var file = Thin(Arm64, UuidCommand(MeasuredUuid), Segment("__TEXT", ("__text", 64)));

        Assert.Equal(MachORejection.NoDebugInfo, MachODebugFile.ToSlices(file).Rejection);
    }

    [Theory]
    [InlineData("__debug_info")]
    [InlineData("__debug_line")]
    public void Dwarf_without_both_info_and_line_is_refused(string only)
    {
        var file = Thin(Arm64, UuidCommand(MeasuredUuid), Segment("__DWARF", (only, 4)));

        Assert.Equal(MachORejection.NoDebugInfo, MachODebugFile.ToSlices(file).Rejection);
    }

    [Fact]
    public void A_dwarf_section_header_with_no_bytes_does_not_count()
    {
        var file = Thin(Arm64, UuidCommand(MeasuredUuid), Segment("__DWARF", ("__debug_info", 0), ("__debug_line", 4)));

        Assert.Equal(MachORejection.NoDebugInfo, MachODebugFile.ToSlices(file).Rejection);
    }

    [Theory]
    [InlineData(5000u)]
    [InlineData(0xFFFFFFF0u)]
    public void A_dwarf_section_whose_offset_is_past_the_file_does_not_count(uint offset)
    {
        var file = Dsym(MeasuredUuid);
        // The __debug_info section is the first in the segment, which follows the 24-byte UUID command.
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(32 + 24 + 72 + 48), offset);

        Assert.Equal(MachORejection.NoDebugInfo, MachODebugFile.ToSlices(file).Rejection);
    }

    [Fact]
    public void Slices_sharing_bytes_are_malformed_so_one_upload_cannot_be_stored_many_times_over()
    {
        // Two valid slices with their own UUIDs, the first declared to run to the end of the file and so
        // over the second: each still parses, so only the overlap rule refuses it. Twenty such slices once
        // stored a 64 MiB upload twenty times over.
        var file = Universal(false, Dsym(MeasuredUuid, X86_64), Dsym(OtherUuid, Arm64));
        var firstOffset = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(8 + 8));
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(8 + 12), (uint)file.Length - firstOffset);

        Assert.Equal(MachORejection.Malformed, MachODebugFile.ToSlices(file).Rejection);
    }

    [Fact]
    public void A_file_with_no_uuid_is_refused()
    {
        var file = Thin(Arm64, Segment("__DWARF", ("__debug_info", 4), ("__debug_line", 4)));

        Assert.Equal(MachORejection.NoUuid, MachODebugFile.ToSlices(file).Rejection);
    }

    [Theory]
    [InlineData(0xFEEDFACEu, false)] // 32-bit little-endian
    [InlineData(0xFEEDFACFu, true)] // 64-bit big-endian
    public void Other_mach_o_layouts_are_unsupported(uint magic, bool bigEndian)
    {
        var file = Dsym(MeasuredUuid);
        if (bigEndian)
        {
            BinaryPrimitives.WriteUInt32BigEndian(file, magic);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(file, magic);
        }

        Assert.Equal(MachORejection.Unsupported, MachODebugFile.ToSlices(file).Rejection);
    }

    [Fact]
    public void A_java_class_file_is_not_taken_for_a_universal_file()
    {
        // Same first four bytes, then the class file version where the architecture count would be.
        byte[] classFile = [0xCA, 0xFE, 0xBA, 0xBE, 0x00, 0x00, 0x00, 0x41, 0, 0, 0, 0];

        Assert.Equal(MachORejection.NotMachO, MachODebugFile.ToSlices(classFile).Rejection);
    }

    [Fact]
    public void Load_commands_longer_than_the_file_are_malformed()
    {
        var file = Dsym(MeasuredUuid);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(20), (uint)file.Length);

        Assert.Equal(MachORejection.Malformed, MachODebugFile.ToSlices(file).Rejection);
    }

    [Fact]
    public void A_command_count_past_the_commands_is_malformed_rather_than_read_on()
    {
        var file = Dsym(MeasuredUuid);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(16), uint.MaxValue);

        Assert.Equal(MachORejection.Malformed, MachODebugFile.ToSlices(file).Rejection);
    }

    [Fact]
    public void A_command_shorter_than_its_own_header_is_malformed()
    {
        var file = Dsym(MeasuredUuid);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(32 + 4), 4);

        Assert.Equal(MachORejection.Malformed, MachODebugFile.ToSlices(file).Rejection);
    }

    [Fact]
    public void A_segment_claiming_more_sections_than_it_holds_is_malformed()
    {
        var file = Dsym(MeasuredUuid);
        // The segment follows the 24-byte UUID command; its section count sits 64 bytes in.
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(32 + 24 + 64), 1000);

        Assert.Equal(MachORejection.Malformed, MachODebugFile.ToSlices(file).Rejection);
    }

    [Fact]
    public void A_universal_slice_past_the_end_of_the_file_is_malformed()
    {
        var file = Universal(false, Dsym(MeasuredUuid));
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(8 + 12), (uint)file.Length);

        Assert.Equal(MachORejection.Malformed, MachODebugFile.ToSlices(file).Rejection);
    }

    [Fact]
    public void Two_slices_naming_one_uuid_are_malformed_since_one_would_overwrite_the_other()
    {
        var file = Universal(false, Dsym(MeasuredUuid, X86_64), Dsym(MeasuredUuid, Arm64));

        Assert.Equal(MachORejection.Malformed, MachODebugFile.ToSlices(file).Rejection);
    }

    [Fact]
    public void A_universal_file_with_one_unusable_slice_is_refused_whole()
    {
        var noDwarf = Thin(X86_64, UuidCommand(OtherUuid), Segment("__TEXT", ("__text", 64)));
        var file = Universal(false, Dsym(MeasuredUuid), noDwarf);

        Assert.Equal(MachORejection.NoDebugInfo, MachODebugFile.ToSlices(file).Rejection);
    }

    [Fact]
    public void Formats_route_elf_and_mach_o_to_their_readers_and_name_the_files_to_send_otherwise()
    {
        var elf = Condux.Tests.Shared.ElfFiles.SymbolsFile("7a3ef9306827da183fa36f07eecef2fb");

        Assert.Null(Assert.Single(DebugFileFormats.Read(elf).Slices!).Architecture);
        Assert.Equal("arm64", Assert.Single(DebugFileFormats.Read(Dsym(MeasuredUuid)).Slices!).Architecture);
        var (slices, rejection) = DebugFileFormats.Read(Encoding.ASCII.GetBytes("{\"not\":\"a debug file\"}"));
        Assert.Null(slices);
        Assert.Contains("--split-debug-info", rejection);
        Assert.Contains("dSYM", rejection);
    }
}
