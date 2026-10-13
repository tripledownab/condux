using System.Text.RegularExpressions;
using Condux.Core.DebugFiles;

namespace Condux.Symbolizer;

/// <summary>
/// The debug files this symbolizer holds, on its own disk, each under the SHA-256 of its bytes. Not under its
/// build ID: a build ID is a note inside the file, which whoever wrote the file chose, so a cache keyed on it
/// would let one uploader's file answer for another's build. A hash can only name the bytes that produce it,
/// so whoever can name a cached file already holds it. The cache is bounded by bytes and by file count, and
/// evicts the least recently used.
/// </summary>
internal sealed partial class DebugFileCache(SymbolizerOptions options)
{
    // A SHA-256 in lowercase hex. Checked before it becomes a file name, so it can never name anything
    // outside the cache directory. \z rather than $, which also matches before a trailing newline.
    [GeneratedRegex(@"\A[0-9a-f]{64}\z")]
    private static partial Regex HashShape();

    private const string TemporarySuffix = ".tmp";

    private readonly Lock _evictLock = new();

    /// <summary>Removes temporaries a killed process left mid-write, which no eviction would ever count.
    /// Safe only because one process owns the directory: the volume is this container's alone.</summary>
    public void RemoveTemporaries()
    {
        if (!Directory.Exists(options.CacheDirectory))
        {
            return;
        }
        foreach (var file in Directory.EnumerateFiles(options.CacheDirectory, "*" + TemporarySuffix))
        {
            File.Delete(file);
        }
    }

    public static bool IsHash(string? value) => value is not null && HashShape().IsMatch(value);

    /// <summary>The file with this hash, marked as used now, or null when it is not held.</summary>
    public string? Find(string hash)
    {
        var path = PathOf(hash);
        try
        {
            File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
            return path;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            // Not held, or evicted a moment ago.
            return null;
        }
    }

    /// <summary>Stores a file under its hash, or answers why it cannot be stored.</summary>
    public string? Store(string hash, byte[] content)
    {
        if (DebugFileHashes.ToSha256(content) != hash)
        {
            return "the file's SHA-256 differs from the one it was sent under";
        }
        var (build, rejection) = ElfDebugFile.ToBuild(content);
        if (build is null)
        {
            return $"not a usable debug file: {rejection}";
        }

        Directory.CreateDirectory(options.CacheDirectory);
        // Written beside the target and renamed into place, so a reader never sees half a file. A file held
        // already has these exact bytes, so replacing it changes nothing a reader could see.
        var temporary = Path.Combine(options.CacheDirectory, $".{hash}.{Guid.NewGuid():N}{TemporarySuffix}");
        try
        {
            File.WriteAllBytes(temporary, content);
            File.Move(temporary, PathOf(hash), overwrite: true);
        }
        catch
        {
            // Eviction counts only stored files, so a temporary left behind would sit outside the cap.
            File.Delete(temporary);
            throw;
        }
        Evict();
        return null;
    }

    // Least recently used first, until the cache fits both bounds. One eviction at a time, since two would
    // each count the other's files and both delete. Stores running at the same moment can each put a file
    // in place before evicting, so the bounds hold once those settle, not at every instant.
    private void Evict()
    {
        lock (_evictLock)
        {
            var files = new DirectoryInfo(options.CacheDirectory).GetFiles("*.debug")
                .OrderBy(f => f.LastAccessTimeUtc)
                .ToList();
            var total = files.Sum(f => f.Length);
            var count = files.Count;
            foreach (var file in files)
            {
                if (total <= options.CacheMaxBytes && count <= options.MaxFiles)
                {
                    break;
                }
                total -= file.Length;
                count--;
                file.Delete();
            }
        }
    }

    private string PathOf(string hash) => IsHash(hash)
        ? Path.Combine(options.CacheDirectory, hash + ".debug")
        : throw new ArgumentException("not a SHA-256", nameof(hash));
}
