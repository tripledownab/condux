using System.Collections.Concurrent;

namespace Condux.Core.Projects;

/// <summary>
/// Wraps a project store with a short-lived in-memory cache so the relay's ingest
/// hot path rarely touches the backing store — keeping the hot path effectively
/// stateless while still sourcing truth from the control-plane's database. Both
/// hits and misses are cached (an invalid DSN must not hit the DB on every event),
/// and both sides are bounded because the caller sizes both. Thread-safe.
/// </summary>
public sealed class CachingProjectStore : IProjectStore
{
    private readonly record struct Entry(Project Value, double ExpiresAt);

    // Neither side may grow with the number of requests, so both are capped, and they are capped
    // separately because what fills them differs in who can do it. A miss needs no credential at all:
    // ingest authenticates before it rate-limits, so an unauthenticated caller decides how many
    // distinct ones to present. A hit needs a working DSN, but not a distinct project: a project id
    // reaches here as the caller spelled it, and Guid.TryParse accepts five formats and ignores case,
    // so one valid DSN can be written as a great many strings that all resolve to the same row. One
    // shared dictionary would let the cheap side evict the other, which is what turns a memory problem
    // into a Postgres one on the path whose outage loses events rather than blocking a page. The hit
    // side is the more generous of the two because overshooting it costs the shared database and
    // undershooting it costs only memory here.
    private const int MaxHits = 16_384;
    private const int MaxMisses = 1024;

    // A count is a memory bound only if an entry has a bounded weight, and a key is two caller-supplied
    // strings. The key half is answered by its shape; this is the project half, which arrives as a URL
    // segment and is otherwise as long as the server's request line allows. A minted DSN carries a UUID
    // there, so nothing legitimate is anywhere near this.
    private const int MaxProjectIdLength = 64;

    private readonly IProjectStore _inner;
    private readonly double _ttlSeconds;
    private readonly Func<double> _clock;
    private readonly ConcurrentDictionary<(string, string), Entry> _hits = new();
    private readonly ConcurrentDictionary<(string, string), double> _misses = new();

    /// <param name="clock">Monotonic seconds source; defaults to process uptime (injected in tests).</param>
    public CachingProjectStore(IProjectStore inner, double ttlSeconds = 60, Func<double>? clock = null)
    {
        _inner = inner;
        _ttlSeconds = ttlSeconds;
        _clock = clock ?? (() => Environment.TickCount64 / 1000.0);
    }

    public async Task<Project?> AuthenticateAsync(string projectId, string publicKey, CancellationToken ct = default)
    {
        // A DSN nobody could have minted cannot match a row, so it costs neither a database round trip
        // nor a cache entry, and what it would have cost is what the caps above are counting.
        if (projectId.Length > MaxProjectIdLength || !DsnKeyGenerator.IsWellFormedPublicKey(publicKey))
        {
            return null;
        }

        var now = _clock();
        var key = (projectId, publicKey);
        if (_hits.TryGetValue(key, out var hit) && hit.ExpiresAt > now)
        {
            return hit.Value;
        }
        if (_misses.TryGetValue(key, out var missUntil) && missUntil > now)
        {
            return null;
        }

        var project = await _inner.AuthenticateAsync(projectId, publicKey, ct);
        if (project is null)
        {
            Remember(_misses, key, now + _ttlSeconds, MaxMisses);
            return null;
        }

        Remember(_hits, key, new Entry(project, now + _ttlSeconds), MaxHits);
        return project;
    }

    // Clears at the cap rather than evicting one entry. Both dictionaries exist so a sender repeating
    // one DSN does not query Postgres per event, and that survives a clear: the next event refills it.
    // Filling one takes a caller varying what it sends, and nothing is owed to that. Counting here
    // walks the dictionary's locks, which is affordable because this runs only after a round trip to
    // the store, never on the hit path.
    //
    // Silent, unlike the other bounded memory in this codebase, and for a reason rather than an
    // oversight: filling the miss side takes 1024 unrecognised credentials, so under the traffic that
    // fills it a line per clear is a log flood the same caller is choosing the size of. What an
    // operator needs is already visible as a 401 rate and as load on the store.
    private static void Remember<T>(ConcurrentDictionary<(string, string), T> cache, (string, string) key, T value, int cap)
    {
        if (cache.Count >= cap)
        {
            cache.Clear();
        }

        cache[key] = value;
    }
}
