using Condux.Core.DebugFiles;

namespace Condux.Symbolizer;

/// <summary>
/// What the symbolizer may spend. Every value bounds something a caller sizes: the file it uploads, the
/// addresses it sends, how long llvm-symbolizer may run on them, and how much disk the cache keeps.
/// </summary>
internal sealed record SymbolizerOptions(
    string LlvmSymbolizerPath,
    string CacheDirectory,
    long CacheMaxBytes,
    int MaxAddresses,
    int MaxFileBytes,
    TimeSpan ResolveTimeout,
    int MaxFiles = SymbolizerOptions.DefaultMaxFiles)
{
    // The count bound, beside the byte bound: eviction lists every file, and a cache of tiny files would
    // otherwise make that list as long as an uploader liked, and spend a disk block on each.
    public const int DefaultMaxFiles = 10_000;

    /// <summary>The debug file upload's own cap, so any file the control-plane accepts can be held.</summary>
    public const int DefaultMaxFileBytes = 64 * 1024 * 1024;

    public static SymbolizerOptions FromConfiguration(IConfiguration config) => new(
        LlvmSymbolizerPath: config["CONDUX_LLVM_SYMBOLIZER"] is { Length: > 0 } path ? path : "llvm-symbolizer",
        CacheDirectory: config["CONDUX_SYMBOLIZER_CACHE_DIR"] is { Length: > 0 } dir ? dir : "/cache",
        CacheMaxBytes: ToCacheMaxBytes(config["CONDUX_SYMBOLIZER_CACHE_MAX_BYTES"]),
        MaxAddresses: SymbolizerLimits.MaxAddressesPerBatch,
        MaxFileBytes: DefaultMaxFileBytes,
        ResolveTimeout: TimeSpan.FromSeconds(10));

    // Unset takes the default. A value that is set must be usable: one that does not parse would otherwise
    // be ignored without a word, and one below the file cap would evict each file as soon as it was stored.
    private static long ToCacheMaxBytes(string? value) => value switch
    {
        null or "" => 2L * 1024 * 1024 * 1024,
        _ when long.TryParse(value, out var bytes) && bytes >= DefaultMaxFileBytes => bytes,
        _ => throw new InvalidOperationException(
            $"CONDUX_SYMBOLIZER_CACHE_MAX_BYTES must be a whole number of bytes, at least {DefaultMaxFileBytes}"),
    };
}
