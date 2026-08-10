using Condux.Core.Plans;
using Condux.Core.Projects;
using Xunit;

namespace Condux.Core.Tests;

public class CachingProjectStoreTests
{
    // Counts inner calls so we can assert the cache actually short-circuits.
    private sealed class CountingStore(Project? result) : IProjectStore
    {
        public int Calls { get; private set; }

        public Task<Project?> AuthenticateAsync(string projectId, string publicKey, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    [Fact]
    public async Task CachesPositiveResultWithinTtl()
    {
        var inner = new CountingStore(new Project("1", Tier.Free));
        var now = 0.0;
        var store = new CachingProjectStore(inner, ttlSeconds: 60, clock: () => now);

        Assert.NotNull(await store.AuthenticateAsync("1", "k"));
        Assert.NotNull(await store.AuthenticateAsync("1", "k")); // served from cache
        Assert.Equal(1, inner.Calls);

        now = 61; // past the TTL → refetch
        Assert.NotNull(await store.AuthenticateAsync("1", "k"));
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task CachesNegativeResultToo()
    {
        var inner = new CountingStore(result: null);
        var store = new CachingProjectStore(inner, ttlSeconds: 60, clock: () => 0);

        Assert.Null(await store.AuthenticateAsync("bad", "key"));
        Assert.Null(await store.AuthenticateAsync("bad", "key")); // a bad DSN must not hit the DB every time
        Assert.Equal(1, inner.Calls);
    }
}
