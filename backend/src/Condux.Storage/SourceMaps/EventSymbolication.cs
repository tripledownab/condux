using System.Text.Json;
using Condux.Core.Events;
using Condux.Storage.ClickHouse;
using Condux.Storage.DebugFiles;
using Microsoft.Extensions.Logging;

namespace Condux.Storage.SourceMaps;

/// <summary>
/// Best-effort read-time symbolication of stored events: source maps rewrite minified JS frames (ADR-0028)
/// and the symbolizer resolves address-only native frames (ADR-0049). Shared by the issue-detail/events
/// endpoints, the MCP tools, the mapping suggestions and both fix paths, so the glue lives in exactly one
/// place. Each pass registers only when what it needs is configured, so either may be null and the call
/// then skips it; callers never branch. A pass that fails leaves the event as the other passes left it, so
/// symbolication can never break a read.
///
/// Every call runs under one time budget, and the native pass under one allowance of bytes written, both
/// shared by every event in the call. Past either, what is not yet symbolicated keeps its stored frames,
/// which costs detail and never the answer. A budget that runs out during an event's native pass costs
/// that event every native frame, the ones already resolved included. The caller's own cancellation is passed on rather than read as that.
/// </summary>
public sealed class EventSymbolication(
    FrameSymbolicator? sourceMaps, NativeSymbolicator? native, ILogger<EventSymbolication> logger)
{
    /// <summary>
    /// How long one call waits for symbols. A page of events would otherwise wait on every file of every
    /// event in turn, and the consumer's auto-fix calls from inside its drain, one event at a time, so a slow
    /// store or symbolizer would hold up ingest for every project.
    /// </summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    public async Task<IReadOnlyList<StoredEvent>> SymbolicateAsync(
        long projectId, IReadOnlyList<StoredEvent> events, CancellationToken cancellationToken = default)
    {
        if ((sourceMaps is null && native is null) || events.Count == 0)
        {
            return events;
        }
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(Budget);
        var allowance = new NativeSymbolicator.Allowance();
        var result = new List<StoredEvent>(events.Count);
        foreach (var stored in events)
        {
            result.Add(limit.IsCancellationRequested
                ? stored
                : await SymbolicateOneAsync(projectId, stored, allowance, limit.Token, cancellationToken));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    /// <summary>
    /// One already-parsed event, for the Conductor's context assembly: the fix paths hold an
    /// <see cref="Event"/>, not a stored row. Like the read path, symbols that cannot be used leave the event
    /// as it was rather than failing the fix request.
    /// </summary>
    public async Task<Event> SymbolicateAsync(long projectId, Event parsed, CancellationToken cancellationToken = default)
    {
        if (sourceMaps is null && native is null)
        {
            return parsed;
        }
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(Budget);
        var result = await RunPassesAsync(
            projectId, parsed, new NativeSymbolicator.Allowance(), limit.Token, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private async Task<StoredEvent> SymbolicateOneAsync(
        long projectId, StoredEvent stored, NativeSymbolicator.Allowance allowance, CancellationToken budget,
        CancellationToken caller)
    {
        try
        {
            if (JsonSerializer.Deserialize<Event>(stored.Payload) is not { } parsed)
            {
                return stored;
            }
            var symbolicated = await RunPassesAsync(projectId, parsed, allowance, budget, caller);
            return ReferenceEquals(symbolicated, parsed)
                ? stored
                : stored with { Payload = JsonSerializer.Serialize(symbolicated) };
        }
        catch (Exception e) when (!caller.IsCancellationRequested)
        {
            logger.LogWarning(e, "Stored event {EventId} could not be symbolicated; keeping it as stored",
                stored.EventId);
            return stored;
        }
    }

    // Source maps first, then native frames; each pass works on what the one before it left.
    private async Task<Event> RunPassesAsync(
        long projectId, Event parsed, NativeSymbolicator.Allowance allowance, CancellationToken budget,
        CancellationToken caller)
    {
        var current = parsed;
        if (sourceMaps is not null)
        {
            current = await TryPassAsync(
                "Source-map", current, ct => sourceMaps.SymbolicateAsync(projectId, current, ct), budget, caller);
        }
        if (native is not null)
        {
            current = await TryPassAsync(
                "Native", current, ct => native.SymbolicateAsync(projectId, current, allowance, ct), budget, caller);
        }
        return current;
    }

    private async Task<Event> TryPassAsync(
        string pass, Event input, Func<CancellationToken, Task<Event>> run, CancellationToken budget,
        CancellationToken caller)
    {
        try
        {
            return await run(budget);
        }
        catch (Exception e) when (!caller.IsCancellationRequested)
        {
            logger.LogWarning(e, "{Pass} symbolication failed for event {EventId}; keeping its frames as they were",
                pass, input.EventId);
            return input;
        }
    }
}
