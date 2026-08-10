using System.Collections.Concurrent;

namespace Condux.Core.Projects;

/// <summary>
/// Wraps a project store with a short-lived in-memory cache so the relay's ingest
/// hot path rarely touches the backing store — keeping the hot path effectively
/// stateless while still sourcing truth from the control-plane's database. Both
/// positive and negative results are cached (an invalid DSN must not hit the DB on
/// every event). Thread-safe.
/// </summary>
public sealed class CachingProjectStore : IProjectStore
{
    private readonly record struct Entry(Project? Value, double ExpiresAt);

    private readonly IProjectStore _inner;
    private readonly double _ttlSeconds;
    private readonly Func<double> _clock;
    private readonly ConcurrentDictionary<(string, string), Entry> _cache = new();

    /// <param name="clock">Monotonic seconds source; defaults to process uptime (injected in tests).</param>
    public CachingProjectStore(IProjectStore inner, double ttlSeconds = 60, Func<double>? clock = null)
    {
        _inner = inner;
        _ttlSeconds = ttlSeconds;
        _clock = clock ?? (() => Environment.TickCount64 / 1000.0);
    }

    public async Task<Project?> AuthenticateAsync(string projectId, string publicKey, CancellationToken ct = default)
    {
        var now = _clock();
        var key = (projectId, publicKey);
        if (_cache.TryGetValue(key, out var entry) && entry.ExpiresAt > now)
        {
            return entry.Value;
        }

        var project = await _inner.AuthenticateAsync(projectId, publicKey, ct);
        _cache[key] = new Entry(project, now + _ttlSeconds);
        return project;
    }
}
