using Condux.Core.DebugFiles;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;

namespace Condux.Storage.DebugFiles;

/// <summary>
/// What the native pass remembers of the symbolizer's answers, per process, so that reading an issue again
/// normally costs the shared symbolizer nothing: the frames each offset of a file resolved to, and for a few
/// minutes the files it refused. Both are keyed by the file's SHA-256, never by a build id or a project, so an
/// answer is shared only between holders of the same bytes, and the symbolizer would give them the same
/// answer. Both are bounded, and they are separate caches so the cheaper one to fill cannot evict the other.
/// A full cache turns new entries away and then drops its least recently read, so an answer it lost costs
/// only another symbolizer call.
/// </summary>
public sealed class SymbolizerCaches : IDisposable
{
    /// <summary>The estimated bytes all kept answers may take. The estimate counts the cache's own entry and
    /// key as well as the frames: counting the frames' strings alone let a full cache hold about half as much
    /// again as this. Measured with the overheads below, a full cache held less than this for answers of no
    /// frames, one, three or ten, and for long names.</summary>
    public const long MaxAnswerBytes = 32L * 1024 * 1024;

    /// <summary>An answer past this is not kept. The file's author chooses the names in it, and a real
    /// answer is a few frames of a function name and a path.</summary>
    public const long MaxEntryBytes = 64 * 1024;

    /// <summary>How many refused files are remembered at once.</summary>
    public const int MaxRefusedFiles = 10_000;

    /// <summary>How long a refused file is not offered again. A refusal depends only on the file's bytes,
    /// so this bounds how long a symbolizer that has changed takes to be asked again.</summary>
    public static readonly TimeSpan RefusalMemory = TimeSpan.FromMinutes(5);

    /// <summary>How long a kept answer is used. An answer depends only on the file's bytes and the
    /// symbolizer's version, and the symbolizer can be upgraded without its callers restarting.</summary>
    public static readonly TimeSpan AnswerMemory = TimeSpan.FromDays(1);

    // Per answer: the cache's entry, the tuple key with its hash string, and the list around the frames.
    private const long EntryOverhead = 400;

    // Per frame: its object, its two ints and its two string headers, besides the characters.
    private const long FrameOverhead = 176;

    private readonly MemoryCache _answers;
    private readonly MemoryCache _refused;

    public SymbolizerCaches()
        : this(TimeProvider.System)
    {
    }

    public SymbolizerCaches(TimeProvider time)
    {
        var clock = new TimeProviderClock(time);
        _answers = new MemoryCache(new MemoryCacheOptions { SizeLimit = MaxAnswerBytes, Clock = clock });
        _refused = new MemoryCache(new MemoryCacheOptions { SizeLimit = MaxRefusedFiles, Clock = clock });
    }

    public bool TryGetFrames(string sha256, ulong offset, out IReadOnlyList<ResolvedFrame> frames)
    {
        if (_answers.TryGetValue((sha256, offset), out IReadOnlyList<ResolvedFrame>? kept) && kept is not null)
        {
            frames = kept;
            return true;
        }
        frames = [];
        return false;
    }

    public void KeepFrames(string sha256, ulong offset, IReadOnlyList<ResolvedFrame> frames)
    {
        var size = ToSize(frames);
        if (size <= MaxEntryBytes)
        {
            _answers.Set((sha256, offset), frames,
                new MemoryCacheEntryOptions { Size = size, AbsoluteExpirationRelativeToNow = AnswerMemory });
        }
    }

    public bool IsRefused(string sha256) => _refused.TryGetValue(sha256, out _);

    public void KeepRefusal(string sha256) =>
        _refused.Set(sha256, true,
            new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = RefusalMemory });

    public void Dispose()
    {
        _answers.Dispose();
        _refused.Dispose();
    }

    private static long ToSize(IReadOnlyList<ResolvedFrame> frames)
    {
        var size = EntryOverhead;
        foreach (var frame in frames)
        {
            size += FrameOverhead + 2L * ((frame.Function?.Length ?? 0) + (frame.File?.Length ?? 0));
        }
        return size;
    }

    // The cache reads its time through this, so a test can move it.
    private sealed class TimeProviderClock(TimeProvider time) : ISystemClock
    {
        public DateTimeOffset UtcNow => time.GetUtcNow();
    }
}
