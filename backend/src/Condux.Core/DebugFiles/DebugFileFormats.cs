namespace Condux.Core.DebugFiles;

/// <summary>
/// Which native debug file an upload is, read into the builds it holds. One entry point for every format,
/// so the upload endpoint takes the same path for an Android symbols file (ELF) and an Apple dSYM (Mach-O),
/// and a caller sent away is told what to send instead, since the caller is a CI log someone reads after a
/// failed build.
/// </summary>
public static class DebugFileFormats
{
    public static (IReadOnlyList<DebugFileSlice>? Slices, string? Rejection) Read(ReadOnlySpan<byte> file)
    {
        if (file.Length >= 4 && file[0] == 0x7F && file[1] == (byte)'E' && file[2] == (byte)'L' && file[3] == (byte)'F')
        {
            var (build, elfRejection) = ElfDebugFile.ToBuild(file);
            return build is null
                ? (null, ToMessage(elfRejection))
                : ([new DebugFileSlice(build, null, 0, file.Length)], null);
        }

        var (slices, machORejection) = MachODebugFile.ToSlices(file);
        return slices is null ? (null, ToMessage(machORejection)) : (slices, null);
    }

    /// <summary>An event's <c>debug_id</c> in the form both formats store it (a lowercase GUID with dashes,
    /// as <see cref="ElfDebugFile.ToDebugId"/> and <see cref="MachODebugFile.ToDebugId"/> write it), or null
    /// when it is not one. Senders vary the spelling (case, no dashes, braces) and may append an age after a dash, as
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

    private static string ToMessage(ElfRejection? rejection) => rejection switch
    {
        ElfRejection.Unsupported => "only little-endian 32- or 64-bit ELF files are supported",
        ElfRejection.NoBuildId => "the file carries no GNU build ID, so no crash could be matched to it",
        ElfRejection.NoDebugInfo =>
            "the file has no DWARF debug info; send the .symbols file, not the stripped library that shipped",
        _ => "the ELF file is malformed",
    };

    private static string ToMessage(MachORejection? rejection) => rejection switch
    {
        MachORejection.NotMachO =>
            "body is neither ELF nor Mach-O; send a .symbols file from --split-debug-info (Android) or the DWARF "
            + "file inside a dSYM, App.framework.dSYM/Contents/Resources/DWARF/App (iOS)",
        MachORejection.Unsupported => "only 64-bit little-endian Mach-O files are supported",
        MachORejection.NoUuid => "the Mach-O file carries no LC_UUID, so no crash could be matched to it",
        MachORejection.NoDebugInfo =>
            "the Mach-O file has no DWARF debug info; send the DWARF file inside the dSYM, not the binary that shipped",
        _ => "the Mach-O file is malformed",
    };
}
