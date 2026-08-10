using Condux.Core.Events;
using Condux.Core.SourceMaps;
using Condux.Storage.Postgres;

namespace Condux.ControlPlane.SourceMaps;

/// <summary>
/// Read-time source-map symbolication (ADR-0028): rewrite a stored event's minified in-app JS frames to
/// their original source position. For each frame it resolves the map debugId-first (a debug_meta image
/// whose code_file matches the frame's abs_path) then by (release, filename), fetches the map from object
/// storage (parsed once per call), and rewrites filename/line/column + function + the original source line.
/// Best-effort per frame: an unmatched or undecodable map leaves the frame untouched.
/// </summary>
public sealed class FrameSymbolicator(SourceMapArtifactRepository artifacts, IObjectStore objectStore)
{
    public async Task<Event> SymbolicateAsync(long projectId, Event stored, CancellationToken cancellationToken = default)
    {
        if (stored.Exceptions.Count == 0)
        {
            return stored;
        }

        var debugImages = BuildDebugImageMap(stored.DebugImages);
        var cache = new Dictionary<string, SourceMap?>();
        var exceptions = new List<ExceptionValue>(stored.Exceptions.Count);
        var changed = false;

        foreach (var ex in stored.Exceptions)
        {
            if (ex.Stacktrace is null || ex.Stacktrace.Frames.Count == 0)
            {
                exceptions.Add(ex);
                continue;
            }

            var frames = new List<Frame>(ex.Stacktrace.Frames.Count);
            foreach (var frame in ex.Stacktrace.Frames)
            {
                var rewritten = await SymbolicateFrameAsync(projectId, stored, frame, debugImages, cache, cancellationToken);
                changed |= !ReferenceEquals(rewritten, frame);
                frames.Add(rewritten);
            }

            exceptions.Add(ex with { Stacktrace = ex.Stacktrace with { Frames = frames } });
        }

        return changed ? stored with { Exceptions = exceptions } : stored;
    }

    private async Task<Frame> SymbolicateFrameAsync(
        long projectId, Event stored, Frame frame, IReadOnlyDictionary<string, string> debugImages,
        Dictionary<string, SourceMap?> cache, CancellationToken cancellationToken)
    {
        if (!frame.InApp || frame.Lineno <= 0)
        {
            return frame;
        }

        var path = frame.AbsPath ?? frame.Filename;
        if (string.IsNullOrEmpty(path) || !LooksLikeJs(path))
        {
            return frame;
        }

        var artifact = await ResolveArtifactAsync(projectId, stored, frame, path, debugImages, cancellationToken);
        if (artifact is null)
        {
            return frame;
        }

        var map = await LoadMapAsync(artifact, cache, cancellationToken);
        // Source-map positions are 0-based; stack frames are 1-based.
        var position = map?.OriginalPositionFor(frame.Lineno - 1, Math.Max(0, frame.Colno - 1));
        if (position is null)
        {
            return frame;
        }

        return frame with
        {
            Filename = position.Source,
            Lineno = position.Line + 1,
            Colno = position.Column + 1,
            Function = position.Name ?? frame.Function,
            ContextLine = position.SourceLine ?? frame.ContextLine,
            ContextBefore = [], // the minified pre/post context no longer applies to the original file
            ContextAfter = [],
        };
    }

    private async Task<SourceMapArtifact?> ResolveArtifactAsync(
        long projectId, Event stored, Frame frame, string path,
        IReadOnlyDictionary<string, string> debugImages, CancellationToken cancellationToken)
    {
        if (debugImages.TryGetValue(path, out var debugId)
            && await artifacts.FindByDebugIdAsync(projectId, debugId, cancellationToken) is { } byDebug)
        {
            return byDebug;
        }

        // Fallback: match on the built file's basename. The upload CLI keys maps by basename, and a browser
        // frame's abs_path is the full deployed URL (often with a cache-buster query), so basename lines the
        // two up where an exact path never would.
        if (!string.IsNullOrEmpty(stored.Release))
        {
            return await artifacts.FindByReleaseFileAsync(projectId, stored.Release, Basename(path), cancellationToken);
        }

        return null;
    }

    private static string Basename(string path)
    {
        var clean = path.Split('?', '#')[0];
        var slash = clean.LastIndexOfAny(['/', '\\']);
        return slash >= 0 ? clean[(slash + 1)..] : clean;
    }

    private async Task<SourceMap?> LoadMapAsync(
        SourceMapArtifact artifact, Dictionary<string, SourceMap?> cache, CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(artifact.ObjectKey, out var cached))
        {
            return cached;
        }

        var bytes = await objectStore.GetAsync(artifact.ObjectKey, cancellationToken);
        var map = bytes is null ? null : SourceMap.Parse(bytes);
        cache[artifact.ObjectKey] = map;
        return map;
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

    private static bool LooksLikeJs(string path)
    {
        var clean = path.Split('?', '#')[0];
        return clean.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
            || clean.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase)
            || clean.EndsWith(".cjs", StringComparison.OrdinalIgnoreCase);
    }
}
