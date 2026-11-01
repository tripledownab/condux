using Condux.Core.Events;
using Condux.Core.SourceMaps;
using Condux.Storage.Postgres;

namespace Condux.Storage.SourceMaps;

/// <summary>
/// Read-time source-map symbolication (ADR-0028): rewrite a stored event's minified in-app JS frames to
/// their original source position. The artifacts an event can reach are resolved up front, so each
/// frame is then a dictionary lookup plus a read of the map it names through a <see cref="SourceMapCache"/>,
/// which a caller symbolicating a page of events shares across them.
/// Best-effort per frame: an unmatched or undecodable map leaves the frame.
/// </summary>
public sealed class FrameSymbolicator(SourceMapArtifactRepository artifacts, IObjectStore objectStore)
{
    public Task<Event> SymbolicateAsync(long projectId, Event stored, CancellationToken cancellationToken = default) =>
        SymbolicateAsync(projectId, stored, new SourceMapCache(), cancellationToken);

    internal async Task<Event> SymbolicateAsync(
        long projectId, Event stored, SourceMapCache cache, CancellationToken cancellationToken)
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
        Frame frame, ReachableArtifacts reachable, SourceMapCache cache, CancellationToken cancellationToken)
    {
        if (reachable.Find(frame) is not { } artifact)
        {
            return frame;
        }

        var map = await cache.GetOrLoadAsync(
            artifact.ObjectKey, ct => LoadMapAsync(artifact.ObjectKey, ct), cancellationToken);
        // Source-map positions are 0-based; stack frames are 1-based.
        var position = map?.OriginalPositionFor(frame.Lineno - 1, Math.Max(0, frame.Colno - 1));
        if (position is null)
        {
            return frame;
        }

        // The map names the file the way its bundler does; the frame has to name it the way the repository
        // does, or neither a code mapping nor the Conductor can find it. A line from an installed package or
        // the bundler, by its path or by the map's ignoreList, stops counting as the app's own, although the
        // minified chunk it sat in did.
        var path = SourceMapPaths.ToRepoPath(position.Source);
        return frame with
        {
            Filename = path,
            InApp = frame.InApp && !position.Ignored && !SourceMapPaths.IsDependency(path),
            Lineno = position.Line + 1,
            Colno = position.Column + 1,
            Function = position.Name ?? frame.Function,
            ContextLine = position.SourceLine ?? frame.ContextLine,
            ContextBefore = [], // the minified pre/post context no longer applies to the original file
            ContextAfter = [],
        };
    }

    private async Task<SourceMap?> LoadMapAsync(string objectKey, CancellationToken cancellationToken)
    {
        var bytes = await objectStore.GetAsync(objectKey, cancellationToken);
        return bytes is null ? null : SourceMap.Parse(bytes);
    }
}
