using System.Globalization;
using System.Text.Json;
using Condux.Core.DebugFiles;
using Condux.Storage.SourceMaps;
using Microsoft.Extensions.Logging;

namespace Condux.Storage.DebugFiles;

/// <summary>
/// The native pass's calls to the symbolizer for one image: hand its file over when the symbolizer does not
/// hold it, resolve the offsets, and check the answer fits the request. A well-formed answer is kept, and a
/// file the symbolizer refuses is remembered (<see cref="SymbolizerCaches"/>). Any failure but a
/// cancellation costs that image alone; a cancellation ends the pass.
/// </summary>
public sealed partial class NativeSymbolicator
{
    private async Task<ResolveResponse?> TryResolveAsync(
        SymbolizerClient symbolizer, DebugFile file, List<ulong> offsets, CancellationToken cancellationToken)
    {
        try
        {
            if (await ResolveAsync(symbolizer, file, offsets, cancellationToken) is not { } resolved)
            {
                return null;
            }
            var (sha256, answer) = resolved;
            if (!IsWellFormed(answer, offsets))
            {
                throw new JsonException("the symbolizer's answer does not match the request");
            }
            for (var i = 0; i < offsets.Count; i++)
            {
                caches.KeepFrames(sha256, offsets[i], answer.Results[i].Frames);
            }
            return answer;
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested)
        {
            metrics.RecordFailure(SymbolicationMetrics.NativePass);
            logger.LogWarning(e, "Native symbolication of build {BuildId} failed; its frames stay as addresses",
                file.BuildId);
            return null;
        }
    }

    // One answer per offset, each naming the address it answers, in the order asked: an answer is kept
    // under its offset, so one given in another order would be kept under the wrong one.
    private static bool IsWellFormed(ResolveResponse answer, List<ulong> asked) =>
        answer.Results is { } results && results.Count == asked.Count
        && results.Select((r, i) => r?.Address == ToAddress(asked[i]) && r.Frames is { } frames
            && frames.All(f => f is not null)).All(ok => ok);

    private static string ToAddress(ulong offset) => "0x" + offset.ToString("x", CultureInfo.InvariantCulture);

    // The answer with the hash it was asked under, or null when there is none to give: a file refused, not
    // read, or evicted since it was checked.
    private async Task<(string Sha256, ResolveResponse Answer)?> ResolveAsync(
        SymbolizerClient symbolizer, DebugFile file, List<ulong> offsets, CancellationToken cancellationToken)
    {
        var sha256 = file.Sha256;
        if (sha256 is not null && caches.IsRefused(sha256))
        {
            return null;
        }
        if (sha256 is null || !await symbolizer.HoldsAsync(sha256, cancellationToken))
        {
            sha256 = await HandOverAsync(symbolizer, file, cancellationToken);
            if (sha256 is null)
            {
                return null;
            }
        }
        var addresses = offsets.Select(ToAddress).ToList();
        return await symbolizer.ResolveAsync(sha256, addresses, cancellationToken) is { } answer
            ? (sha256, answer)
            : null;
    }

    // Reads the whole file and gives it to the symbolizer, which holds it under its hash. A row whose hash
    // is missing, or names other bytes than those stored, gets the hash of what was read, unless the row
    // changed since it was read (CorrectSha256Async). This runs only when the symbolizer does not hold the
    // row's hash, so a row naming bytes it still holds keeps answering from them. The file is held in memory meanwhile, so a gate
    // bounds how many reads do that at once; a busy gate costs this image its frames, not the read. A
    // refusal is remembered under the file's hash, which the row normally holds by now, so the next read
    // neither downloads the file nor offers it again.
    private async Task<string?> HandOverAsync(
        SymbolizerClient symbolizer, DebugFile file, CancellationToken cancellationToken)
    {
        if (!gate.TryEnter())
        {
            logger.LogInformation("Debug file reads are all in use; build {BuildId} stays unresolved", file.BuildId);
            return null;
        }
        try
        {
            var content = await objectStore.GetAsync(file.ObjectKey, cancellationToken);
            if (content is null)
            {
                return null;
            }
            var sha256 = DebugFileHashes.ToSha256(content);
            if (sha256 != file.Sha256)
            {
                await files.CorrectSha256Async(file.Id, file.Sha256, sha256, cancellationToken);
            }
            if (await symbolizer.PutAsync(sha256, content, cancellationToken))
            {
                return sha256;
            }
            caches.KeepRefusal(sha256);
            return null;
        }
        finally
        {
            gate.Leave();
        }
    }
}
