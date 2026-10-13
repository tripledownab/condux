using System.Text.Json;
using Condux.Core.DebugFiles;
using Condux.Core.Events;

namespace Condux.Storage.DebugFiles;

/// <summary>
/// The native pass's rewrite of an event's frames from the answers it got: each resolved frame becomes its
/// inline chain, with its in-app flag read from its path, within the call's byte allowance.
/// </summary>
public sealed partial class NativeSymbolicator
{
    // Each resolved frame becomes its inline chain, written from the crash site backwards until the byte
    // allowance is spent. A frame whose chain does not fit stays as it was, and so does every frame after it.
    private static Event Rewrite(
        Event stored, NativeImages images, Dictionary<(string, ulong), IReadOnlyList<ResolvedFrame>> resolved,
        Allowance allowance)
    {
        var spare = allowance.Spare;
        var exceptions = new ExceptionValue[stored.Exceptions.Count];
        for (var e = stored.Exceptions.Count - 1; e >= 0; e--)
        {
            var ex = stored.Exceptions[e];
            if (ex.Stacktrace is not { Frames.Count: > 0 } stacktrace)
            {
                exceptions[e] = ex;
                continue;
            }
            var frames = new List<Frame>(stacktrace.Frames.Count);
            for (var f = stacktrace.Frames.Count - 1; f >= 0; f--)
            {
                var frame = stacktrace.Frames[f];
                var written = 0;
                if (spare > 0 && Locate(frame, images) is { } key && resolved.TryGetValue(key, out var chain))
                {
                    // Innermost first, as the symbolizer answers; the reverse below puts it last. Written
                    // whole or not at all, so no frame is left marked inlined into one that was cut.
                    var rewrittenChain = new List<Frame>(chain.Count);
                    var size = 0;
                    for (var i = 0; i < chain.Count && size <= spare; i++)
                    {
                        var location = chain[i];
                        var rewritten = frame with
                        {
                            Function = location.Function,
                            Filename = location.File,
                            Lineno = location.Line,
                            Colno = location.Column,
                            InApp = DartSourcePaths.ToInApp(location.File) ?? frame.InApp,
                            // Every entry but the last, the function the code was compiled into, was inlined.
                            Inlined = i < chain.Count - 1,
                        };
                        size += JsonSerializer.SerializeToUtf8Bytes(rewritten).Length;
                        rewrittenChain.Add(rewritten);
                    }
                    if (size > spare)
                    {
                        spare = 0;
                    }
                    else
                    {
                        spare -= size;
                        frames.AddRange(rewrittenChain);
                        written = rewrittenChain.Count;
                    }
                }
                if (written == 0)
                {
                    frames.Add(frame);
                }
            }
            frames.Reverse();
            exceptions[e] = ex with { Stacktrace = stacktrace with { Frames = frames } };
        }
        allowance.Spare = spare;
        return stored with { Exceptions = exceptions };
    }
}
