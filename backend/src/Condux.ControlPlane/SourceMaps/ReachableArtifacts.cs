using System.Collections.ObjectModel;
using Condux.Core.Events;
using Condux.Core.SourceMaps;
using Condux.Storage.Postgres;

namespace Condux.ControlPlane.SourceMaps;

/// <summary>
/// Which uploaded source map, if any, each frame of one event belongs to (ADR-0028). Resolved once for
/// the whole event in two queries, and everything about reading a frame's path lives here so the walk
/// that collects the lookup keys and the walk that uses them cannot disagree about which frames count.
/// </summary>
internal sealed class ReachableArtifacts
{
    // What one event's frame list may cost. That list is written by whoever sent the event and is only
    // as short as they choose, so the number of maps this fetches has to be a number of ours. A minified
    // stack spans a handful of chunks, and the keys are taken from the crash site backwards, so when the
    // cap bites it drops the entry points rather than the failure.
    private const int MaxLookupKeys = 32;

    private readonly IReadOnlyDictionary<string, string> _debugImages;
    private readonly IReadOnlyDictionary<string, SourceMapArtifact> _byDebugId;
    private readonly IReadOnlyDictionary<string, SourceMapArtifact> _byFilename;

    private ReachableArtifacts(
        IReadOnlyDictionary<string, string> debugImages,
        IReadOnlyDictionary<string, SourceMapArtifact> byDebugId,
        IReadOnlyDictionary<string, SourceMapArtifact> byFilename)
    {
        _debugImages = debugImages;
        _byDebugId = byDebugId;
        _byFilename = byFilename;
    }

    public bool IsEmpty => _byDebugId.Count == 0 && _byFilename.Count == 0;

    public static async Task<ReachableArtifacts> ResolveAsync(
        SourceMapArtifactRepository artifacts, long projectId, Event stored, CancellationToken cancellationToken)
    {
        var debugImages = BuildDebugImageMap(stored.DebugImages);
        var debugIds = new HashSet<string>();
        var filenames = new HashSet<string>();
        CollectLookupKeys(stored, debugImages, debugIds, filenames);

        return new ReachableArtifacts(
            debugImages,
            await artifacts.FindByDebugIdsAsync(projectId, debugIds, cancellationToken),
            string.IsNullOrEmpty(stored.Release)
                ? ReadOnlyDictionary<string, SourceMapArtifact>.Empty
                : await artifacts.FindByReleaseFilesAsync(projectId, stored.Release, filenames, cancellationToken));
    }

    /// <summary>The map this frame should be rewritten by, or null when it has none or is not a frame
    /// we de-minify at all.</summary>
    public SourceMapArtifact? Find(Frame frame)
    {
        if (SymbolicatablePath(frame) is not { } path)
        {
            return null;
        }
        if (_debugImages.TryGetValue(path, out var debugId) && _byDebugId.TryGetValue(debugId, out var byDebug))
        {
            return byDebug;
        }

        // Fallback: match on the built file's basename. The upload CLI keys maps by basename, and a
        // browser frame's abs_path is the full deployed URL (often with a cache-buster query), so
        // basename lines the two up where an exact path never would.
        return _byFilename.GetValueOrDefault(Basename(path));
    }

    // Walked from the end in both directions, because that is where the relevance is: frames arrive
    // oldest-first so the crash site is the last one, and the dashboard's primaryException takes the
    // LAST exception of a chain. Collecting forwards let an earlier exception fill the cap and leave
    // the frames a reader opened the issue to see with no map. Both keys are collected for a frame,
    // because a debug id nothing was uploaded for still falls back to the release and file name.
    private static void CollectLookupKeys(
        Event stored, IReadOnlyDictionary<string, string> debugImages,
        HashSet<string> debugIds, HashSet<string> filenames)
    {
        for (var e = stored.Exceptions.Count - 1; e >= 0; e--)
        {
            var frames = stored.Exceptions[e].Stacktrace?.Frames;
            for (var i = (frames?.Count ?? 0) - 1; i >= 0; i--)
            {
                if (debugIds.Count >= MaxLookupKeys && filenames.Count >= MaxLookupKeys)
                {
                    return;
                }
                if (SymbolicatablePath(frames![i]) is not { } path)
                {
                    continue;
                }
                if (debugImages.TryGetValue(path, out var debugId) && debugIds.Count < MaxLookupKeys)
                {
                    debugIds.Add(debugId);
                }
                if (filenames.Count < MaxLookupKeys)
                {
                    filenames.Add(Basename(path));
                }
            }
        }
    }

    private static string? SymbolicatablePath(Frame frame)
    {
        if (!frame.InApp || frame.Lineno <= 0)
        {
            return null;
        }

        var path = frame.AbsPath ?? frame.Filename;
        return !string.IsNullOrEmpty(path) && LooksLikeJs(path) ? path : null;
    }

    // Spans rather than Split, because these run per frame in both walks and the frame count is the
    // sender's to choose. Split allocated an array and a substring to answer a question that needs
    // neither; Basename still allocates the one string it returns, which is the answer itself.
    private static ReadOnlySpan<char> WithoutQuery(string path)
    {
        var cut = path.AsSpan().IndexOfAny('?', '#');
        return cut >= 0 ? path.AsSpan(0, cut) : path.AsSpan();
    }

    private static string Basename(string path)
    {
        var clean = WithoutQuery(path);
        var slash = clean.LastIndexOfAny('/', '\\');
        return slash >= 0 ? clean[(slash + 1)..].ToString() : clean.ToString();
    }

    private static bool LooksLikeJs(string path)
    {
        var clean = WithoutQuery(path);
        return clean.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
            || clean.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase)
            || clean.EndsWith(".cjs", StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> BuildDebugImageMap(IReadOnlyList<DebugImage> images)
    {
        var map = new Dictionary<string, string>();
        foreach (var image in images)
        {
            // Only a source-map image points at an uploaded map; a native debug image (macho/elf/...) does not.
            if (string.Equals(image.Type, "sourcemap", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(image.CodeFile) && !string.IsNullOrEmpty(image.DebugId))
            {
                map[image.CodeFile] = image.DebugId;
            }
        }

        return map;
    }
}
