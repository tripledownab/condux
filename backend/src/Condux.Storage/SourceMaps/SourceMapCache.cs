using Condux.Core.SourceMaps;

namespace Condux.Storage.SourceMaps;

/// <summary>
/// The parsed source maps one symbolication call has read, keyed by object key, so a page of events that
/// share a map fetches and parses it once while it stays cached, rather than once per event. Least recently
/// used goes first, and
/// the cap is the most maps one event can reach, so a single event's maps never evict each other and the
/// call holds no more parsed maps at once than one event could before the cache was shared. A miss is
/// kept too, so an absent object is not fetched again. Used by one call at a time, never concurrently.
/// </summary>
public sealed class SourceMapCache
{
    private readonly int _capacity;
    private readonly Dictionary<string, LinkedListNode<(string Key, SourceMap? Map)>> _entries = [];
    private readonly LinkedList<(string Key, SourceMap? Map)> _recency = new();

    public SourceMapCache(int capacity = ReachableArtifacts.MaxArtifactsPerEvent)
    {
        _capacity = capacity;
    }

    public async Task<SourceMap?> GetOrLoadAsync(
        string objectKey, Func<CancellationToken, Task<SourceMap?>> load, CancellationToken cancellationToken)
    {
        if (_entries.TryGetValue(objectKey, out var node))
        {
            _recency.Remove(node);
            _recency.AddFirst(node);
            return node.Value.Map;
        }

        // Evicted before the load, so the map being parsed is never one over the cap.
        if (_entries.Count >= _capacity && _recency.Last is { } oldest)
        {
            _entries.Remove(oldest.Value.Key);
            _recency.RemoveLast();
        }
        var map = await load(cancellationToken);
        _entries[objectKey] = _recency.AddFirst((objectKey, map));
        return map;
    }
}
