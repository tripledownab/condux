using Condux.Core.DebugFiles;
using Condux.Symbolizer;
using Xunit;
using static Condux.Tests.Shared.ElfFiles;

namespace Condux.Symbolizer.Tests;

/// <summary>The cache holds a file only under the SHA-256 of its bytes, never lets a key name a path outside
/// its directory, and stays under its byte and file caps by evicting the least recently used.</summary>
public sealed class DebugFileCacheTests : IDisposable
{
    private static readonly byte[] FileA = SymbolsFile("7a3ef9306827da183fa36f07eecef2fb");
    private static readonly byte[] FileB = SymbolsFile("0123456789abcdef0123456789abcdef");
    private static readonly string HashA = DebugFileHashes.ToSha256(FileA);
    private static readonly string HashB = DebugFileHashes.ToSha256(FileB);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "symbolizer-cache-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private DebugFileCache Cache(long maxBytes = 1 << 30, int maxFiles = SymbolizerOptions.DefaultMaxFiles) =>
        new(new SymbolizerOptions("llvm-symbolizer", _directory, maxBytes, 512, 1 << 20, TimeSpan.FromSeconds(1), maxFiles));

    [Fact]
    public void A_file_is_found_under_its_own_hash()
    {
        var cache = Cache();

        Assert.Null(cache.Store(HashA, FileA));

        Assert.NotNull(cache.Find(HashA));
        Assert.Null(cache.Find(HashB));
    }

    [Fact]
    public void A_file_sent_under_another_hash_is_refused()
    {
        var cache = Cache();

        Assert.NotNull(cache.Store(HashB, FileA));
        Assert.Null(cache.Find(HashB));
    }

    [Fact]
    public void Two_files_claiming_one_build_id_are_held_apart()
    {
        // A build ID is whatever the file's author wrote, so a second file can claim the first one's. Neither
        // may answer for the other, and neither may replace the other.
        const string sharedBuild = "7a3ef9306827da183fa36f07eecef2fb";
        var genuine = SymbolsFile(sharedBuild);
        var forged = SymbolsFile(sharedBuild, wide: false);
        var cache = Cache();

        cache.Store(DebugFileHashes.ToSha256(genuine), genuine);
        cache.Store(DebugFileHashes.ToSha256(forged), forged);

        Assert.Equal(genuine, File.ReadAllBytes(cache.Find(DebugFileHashes.ToSha256(genuine))!));
        Assert.Equal(forged, File.ReadAllBytes(cache.Find(DebugFileHashes.ToSha256(forged))!));
    }

    [Fact]
    public void A_file_that_is_not_a_debug_file_is_refused()
    {
        var notElf = "not elf"u8.ToArray();

        Assert.NotNull(Cache().Store(DebugFileHashes.ToSha256(notElf), notElf));
    }

    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData("7a3ef9306827da183fa36f07eecef2fb")]
    [InlineData("F7B810B31AA91009485BB20F4F1698F11906948C5FC54FE1BEC1FA2A88D81F38")]
    [InlineData("f7b810b31aa91009485bb20f4f1698f11906948c5fc54fe1bec1fa2a88d81f38\n")]
    [InlineData("")]
    public void Only_a_lowercase_sha256_is_a_key(string value)
    {
        Assert.False(DebugFileCache.IsHash(value));
        Assert.Throws<ArgumentException>(() => Cache().Find(value));
    }

    [Fact]
    public void The_least_recently_used_file_is_evicted_to_fit_the_byte_cap()
    {
        var cache = Cache(maxBytes: FileA.Length + FileA.Length / 2);
        cache.Store(HashA, FileA);
        File.SetLastAccessTimeUtc(cache.Find(HashA)!, DateTime.UtcNow.AddHours(-1));

        cache.Store(HashB, FileB);

        Assert.Null(cache.Find(HashA));
        Assert.NotNull(cache.Find(HashB));
    }

    [Fact]
    public void The_least_recently_used_file_is_evicted_to_fit_the_file_cap()
    {
        var cache = Cache(maxFiles: 1);
        cache.Store(HashA, FileA);
        File.SetLastAccessTimeUtc(cache.Find(HashA)!, DateTime.UtcNow.AddHours(-1));

        cache.Store(HashB, FileB);

        Assert.Null(cache.Find(HashA));
        Assert.NotNull(cache.Find(HashB));
    }

    [Fact]
    public void A_write_that_fails_leaves_no_temporary_behind()
    {
        // A directory where the stored file belongs makes the rename fail after the bytes are written.
        Directory.CreateDirectory(Path.Combine(_directory, HashA + ".debug"));

        Assert.ThrowsAny<IOException>(() => Cache().Store(HashA, FileA));

        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public void Temporaries_a_killed_process_left_are_removed_and_stored_files_kept()
    {
        var cache = Cache();
        cache.Store(HashA, FileA);
        var leftover = Path.Combine(_directory, $".{HashB}.{Guid.NewGuid():N}.tmp");
        File.WriteAllBytes(leftover, [1, 2, 3]);

        cache.RemoveTemporaries();

        Assert.False(File.Exists(leftover));
        Assert.NotNull(cache.Find(HashA));
    }

    [Fact]
    public void A_cache_whose_directory_is_gone_holds_nothing()
    {
        Assert.Null(Cache().Find(HashA));
    }
}
