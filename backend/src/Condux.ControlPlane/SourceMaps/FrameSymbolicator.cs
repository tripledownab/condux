using Condux.Core.Events;
using Condux.Core.SourceMaps;
using Condux.Storage.Postgres;

namespace Condux.ControlPlane.SourceMaps;

/// <summary>
/// Read-time source-map symbolication (ADR-0028): rewrite a stored event's minified in-app JS frames to
/// their original source position. The artifacts an event can reach are resolved up front, so each
/// frame is then a dictionary lookup plus at most one fetch of the map it names (parsed once per call).
/// Best-effort per frame: an unmatched or undecodable map leaves the frame.
/// </summary>
public sealed class FrameSymbolicator(SourceMapArtifactRepository artifacts, IObjectStore objectStore)
{
    public async Task<Event> SymbolicateAsync(long projectId, Event stored, CancellationToken cancellationToken = default)
    {
        if (stored.Exceptions.Count == 0)
        {
            return stored;
        }

        var reachable = await ReachableArtifacts.ResolveAsync(artifacts, projectId, stored, cancellationToken);
        if (reachable.IsEmpty)
        {
            return stored;
        }

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
                var rewritten = await SymbolicateFrameAsync(frame, reachable, cache, cancellationToken);
                changed |= !ReferenceEquals(rewritten, frame);
                frames.Add(rewritten);
            }

            exceptions.Add(ex with { Stacktrace = ex.Stacktrace with { Frames = frames } });
        }

        return changed ? stored with { Exceptions = exceptions } : stored;
    }

    private async Task<Frame> SymbolicateFrameAsync(
        Frame frame, ReachableArtifacts reachable, Dictionary<string, SourceMap?> cache,
        CancellationToken cancellationToken)
    {
        if (reachable.Find(frame) is not { } artifact)
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
}
