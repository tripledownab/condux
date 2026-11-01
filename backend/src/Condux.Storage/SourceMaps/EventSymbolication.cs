using System.Diagnostics;
using Condux.Core.Events;
using Condux.Storage.ClickHouse;
using Condux.Storage.DebugFiles;
using Microsoft.Extensions.Logging;

namespace Condux.Storage.SourceMaps;

/// <summary>
/// Best-effort read-time symbolication of stored events: source maps rewrite minified JS frames (ADR-0028),
/// the symbolizer resolves address-only native frames (ADR-0049), and a Flutter build's obfuscation map
/// restores its class names in exception types and messages. Shared by the issue-detail/events endpoints,
/// the MCP tools, the mapping suggestions and both fix paths, so the glue lives in exactly one place. Each
/// pass registers only when what it needs is configured, so any may be null and the call then skips it;
/// callers never branch. A pass that fails leaves the event as the other passes left it, so
/// symbolication can never break a read.
///
/// Every call runs under one time budget, and the native pass under one allowance of bytes written, both
/// shared by every event in the call. Past the budget or the allowance, what is not yet symbolicated keeps
/// its stored frames, which costs detail and never the answer. A budget that runs out during an event's
/// native pass costs that event every native frame, the ones already resolved included. The caller's own
/// cancellation is passed on rather than read as that. The source-map pass's <see cref="SourceMapCache"/> is
/// shared across the call too, so a map the events share is read and parsed again only if the cache evicted
/// it or its read failed.
/// </summary>
public sealed class EventSymbolication(
    FrameSymbolicator? sourceMaps, NativeSymbolicator? native, DartTypeNames? dartTypes,
    SymbolicationMetrics metrics, ILogger<EventSymbolication> logger)
{
    /// <summary>
    /// How long one call waits for symbols unless its caller passes a budget of its own. A page of events would
    /// otherwise wait on every file of every event in turn, so a slow store or symbolizer would hold a reader
    /// for as long as it took. A caller that blocks other work while it waits passes a shorter one.
    /// </summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    public async Task<IReadOnlyList<StoredEvent>> SymbolicateAsync(
        long projectId, IReadOnlyList<StoredEvent> events, CancellationToken cancellationToken = default)
    {
        if ((sourceMaps is null && native is null && dartTypes is null) || events.Count == 0)
        {
            return events;
        }
        var started = Stopwatch.GetTimestamp();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(Budget);
        var allowance = new NativeSymbolicator.Allowance();
        var maps = new SourceMapCache();
        var result = new List<StoredEvent>(events.Count);
        foreach (var stored in events)
        {
            result.Add(limit.IsCancellationRequested
                ? stored
                : await SymbolicateOneAsync(projectId, stored, allowance, maps, limit.Token, cancellationToken));
        }
        cancellationToken.ThrowIfCancellationRequested();
        metrics.RecordDuration(Stopwatch.GetElapsedTime(started));
        return result;
    }

    /// <summary>
    /// One already-parsed event, for the Conductor's context assembly: the fix paths hold an
    /// <see cref="Event"/>, not a stored row. Like the read path, symbols that cannot be used leave the event
    /// as it was rather than failing the fix request.
    /// </summary>
    public Task<Event> SymbolicateAsync(long projectId, Event parsed, CancellationToken cancellationToken = default) =>
        SymbolicateAsync(projectId, parsed, Budget, cancellationToken);

    /// <summary>The same, waiting at most <paramref name="budget"/> for symbols, after which the event keeps
    /// what has not been symbolicated yet as it was stored.</summary>
    public async Task<Event> SymbolicateAsync(
        long projectId, Event parsed, TimeSpan budget, CancellationToken cancellationToken = default)
    {
        if (sourceMaps is null && native is null && dartTypes is null)
        {
            return parsed;
        }
        var started = Stopwatch.GetTimestamp();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(budget);
        var result = await RunPassesAsync(
            projectId, parsed, new NativeSymbolicator.Allowance(), new SourceMapCache(), limit.Token, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        metrics.RecordDuration(Stopwatch.GetElapsedTime(started));
        return result;
    }

    private async Task<StoredEvent> SymbolicateOneAsync(
        long projectId, StoredEvent stored, NativeSymbolicator.Allowance allowance, SourceMapCache maps,
        CancellationToken budget, CancellationToken caller)
    {
        try
        {
            if (stored.ToEvent() is not { } parsed)
            {
                return stored;
            }
            var symbolicated = await RunPassesAsync(projectId, parsed, allowance, maps, budget, caller);
            return ReferenceEquals(symbolicated, parsed)
                ? stored
                : stored with { Payload = EventJson.ToJson(symbolicated) };
        }
        catch (Exception e) when (!caller.IsCancellationRequested)
        {
            metrics.RecordFailure(SymbolicationMetrics.EventPass);
            logger.LogWarning(e, "Stored event {EventId} could not be symbolicated; keeping it as stored",
                stored.EventId);
            return stored;
        }
    }

    // Source maps first, then native frames, then obfuscated Dart class names; each pass works on what the
    // one before it left.
    private async Task<Event> RunPassesAsync(
        long projectId, Event parsed, NativeSymbolicator.Allowance allowance, SourceMapCache maps,
        CancellationToken budget, CancellationToken caller)
    {
        var current = parsed;
        if (sourceMaps is not null)
        {
            current = await TryPassAsync(
                SymbolicationMetrics.SourceMapPass, current,
                ct => sourceMaps.SymbolicateAsync(projectId, current, maps, ct), budget, caller);
        }
        // A budget the source-map pass ran out stops here, rather than failing the native pass a second time.
        if (native is not null && !budget.IsCancellationRequested)
        {
            current = await TryPassAsync(
                SymbolicationMetrics.NativePass, current,
                ct => native.SymbolicateAsync(projectId, current, allowance, ct), budget, caller);
        }
        if (dartTypes is not null && !budget.IsCancellationRequested)
        {
            current = await TryPassAsync(
                SymbolicationMetrics.DartTypesPass, current,
                ct => dartTypes.SymbolicateAsync(projectId, current, ct), budget, caller);
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
            metrics.RecordFailure(pass);
            logger.LogWarning(e, "{Pass} symbolication failed for event {EventId}; keeping its frames as they were",
                pass, input.EventId);
            return input;
        }
    }
}
