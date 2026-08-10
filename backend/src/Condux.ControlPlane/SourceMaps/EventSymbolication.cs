using System.Text.Json;
using Condux.Core.Events;
using Condux.Storage.ClickHouse;

namespace Condux.ControlPlane.SourceMaps;

/// <summary>
/// Best-effort read-time symbolication (ADR-0028) of stored events: deserialize each event, rewrite its
/// minified in-app JS frames via the project's source maps, reserialize. Shared by the issue-detail/events
/// endpoints and the MCP tools so the de-minification glue lives in exactly one place. The
/// <see cref="FrameSymbolicator"/> only registers when object storage is configured, so this is injected
/// with an optional one: when it is null (storage off) the call is a no-op, so callers never branch. A
/// per-event failure returns that event unchanged, so symbolication can never break a read.
/// </summary>
internal sealed class EventSymbolication(FrameSymbolicator? symbolicator, ILogger<EventSymbolication> logger)
{
    public async Task<IReadOnlyList<StoredEvent>> SymbolicateAsync(
        long projectId, IReadOnlyList<StoredEvent> events, CancellationToken cancellationToken = default)
    {
        if (symbolicator is null || events.Count == 0)
        {
            return events;
        }
        var result = new List<StoredEvent>(events.Count);
        foreach (var stored in events)
        {
            result.Add(await SymbolicateOneAsync(symbolicator, projectId, stored, cancellationToken));
        }
        return result;
    }

    private async Task<StoredEvent> SymbolicateOneAsync(
        FrameSymbolicator frameSymbolicator, long projectId, StoredEvent stored, CancellationToken cancellationToken)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<Event>(stored.Payload);
            if (parsed is null)
            {
                return stored;
            }
            var symbolicated = await frameSymbolicator.SymbolicateAsync(projectId, parsed, cancellationToken);
            return ReferenceEquals(symbolicated, parsed)
                ? stored
                : stored with { Payload = JsonSerializer.Serialize(symbolicated) };
        }
        catch (Exception e)
        {
            logger.LogWarning(
                e, "Source-map symbolication failed for event {EventId}; returning the raw payload", stored.EventId);
            return stored;
        }
    }
}
