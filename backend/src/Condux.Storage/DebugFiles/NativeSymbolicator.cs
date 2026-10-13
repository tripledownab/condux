using System.Globalization;
using System.Text.Json;
using Condux.Core.DebugFiles;
using Condux.Core.Events;
using Condux.Core.SourceMaps;
using Condux.Storage.Postgres;
using Microsoft.Extensions.Logging;

namespace Condux.Storage.DebugFiles;

/// <summary>
/// Read-time native symbolication (ADR-0049): a frame that carries only an instruction address is placed in
/// its image by the rule grouping uses (<see cref="NativeImages"/>), the image's debug file is found by its
/// debug id, and the symbolizer resolves the offset to function, file and line. An inlined call becomes
/// several frames, innermost last (keeping the stack oldest first), each but the outermost marked inlined.
/// A Dart frame takes its in-app flag from its path (<see cref="DartSourcePaths"/>).
///
/// The sender chooses the frame and image counts and the file's author chooses the names, so the work is
/// capped: a few of the images that have an uploaded file, per image no more offsets than the symbolizer
/// takes in one batch, and a byte allowance for the frames written, shared by every event in one call. Each
/// cap is spent from the crash site backwards, so the frames someone
/// opened the issue to read are the ones resolved. Best-effort per image: one with no file, a file the
/// symbolizer refuses, or a call that fails leaves that image's frames as addresses and the others still
/// resolve, since one hostile file should not cost the rest of the stack. An offset the process has already
/// resolved for the same file is answered from <see cref="SymbolizerCaches"/> while the cache still holds
/// it, so an offset is normally asked once across the events of one call and across reads, and a file the
/// symbolizer refused is not offered again for a while.
/// </summary>
public sealed partial class NativeSymbolicator(
    DebugFileRepository files, IObjectStore objectStore, IHttpClientFactory clients, DebugFileReadGate gate,
    SymbolizerCaches caches, SymbolizerMetrics metrics, ILogger<NativeSymbolicator> logger)
{
    public const int MaxImages = 4;

    /// <summary>One resolve per image, so an image takes no more offsets than one batch carries.</summary>
    public const int MaxOffsetsPerImage = SymbolizerLimits.MaxAddressesPerBatch;

    /// <summary>The images one event may look up, before the few with an uploaded file are chosen. A native
    /// crash can stop in system libraries nobody uploads files for, and choosing before the lookup would then
    /// spend every place on them and never reach the app's own frames.</summary>
    public const int MaxCandidateImages = 32;

    /// <summary>
    /// The JSON the pass may write in one call, across every event in it (<see cref="Allowance"/>). An answer
    /// is copied into every frame that repeats its address, so without it the sender's repeat count times
    /// the length of the file's names, times the events on a page, would size the answer. Past it the
    /// remaining frames stay as addresses.
    /// </summary>
    public const int MaxWrittenBytes = 1 << 20;

    /// <summary>What is left of <see cref="MaxWrittenBytes"/> for one call. A caller takes one per call and
    /// passes it with every event, so a page shares one allowance rather than holding one per event.</summary>
    public sealed class Allowance
    {
        internal int Spare { get; set; } = MaxWrittenBytes;
    }

    public async Task<Event> SymbolicateAsync(
        long projectId, Event stored, Allowance allowance, CancellationToken cancellationToken = default)
    {
        var images = NativeImages.From(stored.DebugImages);
        var candidates = ToCandidates(stored, images);
        if (candidates.Count == 0 || allowance.Spare <= 0)
        {
            return stored;
        }

        var found = await files.FindAsync(projectId, candidates, cancellationToken);
        var wanted = Collect(stored, images, found);
        // A client per pass rather than one held for good, so the factory can rotate its handler.
        var symbolizer = new SymbolizerClient(clients.CreateClient(SymbolizerClient.HttpClientName), metrics);
        var resolved = new Dictionary<(string DebugId, ulong Offset), IReadOnlyList<ResolvedFrame>>();
        foreach (var (debugId, offsets) in wanted)
        {
            var file = found[debugId];
            var missing = new List<ulong>(offsets.Count);
            foreach (var offset in offsets)
            {
                if (file.Sha256 is { } sha256 && caches.TryGetFrames(sha256, offset, out var kept))
                {
                    resolved[(debugId, offset)] = kept;
                }
                else
                {
                    missing.Add(offset);
                }
            }
            if (missing.Count == 0
                || await TryResolveAsync(symbolizer, file, missing, cancellationToken) is not { } answer)
            {
                continue;
            }
            for (var i = 0; i < missing.Count; i++)
            {
                resolved[(debugId, missing[i])] = answer.Results[i].Frames;
            }
        }

        return resolved.Count == 0 ? stored : Rewrite(stored, images, resolved, allowance);
    }

    // Every frame the pass could resolve, with its image and offset, from the last exception's crash site
    // backwards, which is the order every cap below spends in.
    private static IEnumerable<(string DebugId, ulong Offset)> ToLocated(Event stored, NativeImages images)
    {
        for (var e = stored.Exceptions.Count - 1; e >= 0; e--)
        {
            var frames = stored.Exceptions[e].Stacktrace?.Frames ?? [];
            for (var f = frames.Count - 1; f >= 0; f--)
            {
                if (Locate(frames[f], images) is { } located)
                {
                    yield return located;
                }
            }
        }
    }

    private static List<string> ToCandidates(Event stored, NativeImages images)
    {
        var candidates = new List<string>();
        foreach (var (debugId, _) in ToLocated(stored, images))
        {
            if (!candidates.Contains(debugId))
            {
                candidates.Add(debugId);
                if (candidates.Count == MaxCandidateImages)
                {
                    break;
                }
            }
        }
        return candidates;
    }

    // The offsets to resolve, per image that has an uploaded file.
    private static Dictionary<string, List<ulong>> Collect(
        Event stored, NativeImages images, IReadOnlyDictionary<string, DebugFile> found)
    {
        var wanted = new Dictionary<string, List<ulong>>(StringComparer.Ordinal);
        var seen = new HashSet<(string, ulong)>();
        foreach (var located in ToLocated(stored, images))
        {
            var (debugId, offset) = located;
            if (!found.ContainsKey(debugId))
            {
                continue;
            }
            if (!wanted.TryGetValue(debugId, out var offsets))
            {
                if (wanted.Count == MaxImages)
                {
                    continue;
                }
                wanted[debugId] = offsets = [];
            }
            if (offsets.Count < MaxOffsetsPerImage && seen.Add(located))
            {
                offsets.Add(offset);
            }
        }
        return wanted;
    }

    // A frame grouping keys on its address, inside a sized image the event lists with a debug id. The size
    // matters: the symbolizer names an address past the image after some symbol of the file, a guess its
    // answer cannot tell from a real frame.
    private static (string DebugId, ulong Offset)? Locate(Frame frame, NativeImages images) =>
        NativeImages.IsAddressOnly(frame)
        && images.Place(frame) is { Image: { DebugId: { } id, ImageSize: long size and > 0 } } placed
        && placed.Offset < (ulong)size
        && ElfDebugFile.ToStoredDebugId(id) is { } debugId
            ? (debugId, placed.Offset)
            : null;
}
