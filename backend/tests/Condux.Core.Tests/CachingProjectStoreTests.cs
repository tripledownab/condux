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

    // One key resolves, everything else does not: the shape a real deployment has, and the only shape
    // that can show whether a flood of strangers costs the tenant who is actually sending events.
    private sealed class OneKeyStore(string publicKey) : IProjectStore
    {
        public int Calls { get; private set; }

        public Task<Project?> AuthenticateAsync(string projectId, string key, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(key == publicKey ? new Project("1", Tier.Free) : null);
        }
    }

    private static readonly string Key = DsnKeyGenerator.NewPublicKey();

    [Fact]
    public async Task CachesPositiveResultWithinTtl()
    {
        var inner = new CountingStore(new Project("1", Tier.Free));
        var now = 0.0;
        var store = new CachingProjectStore(inner, ttlSeconds: 60, clock: () => now);

        Assert.NotNull(await store.AuthenticateAsync("1", Key));
        Assert.NotNull(await store.AuthenticateAsync("1", Key)); // served from cache
        Assert.Equal(1, inner.Calls);

        now = 61; // past the TTL → refetch
        Assert.NotNull(await store.AuthenticateAsync("1", Key));
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task CachesNegativeResultToo()
    {
        var inner = new CountingStore(result: null);
        var store = new CachingProjectStore(inner, ttlSeconds: 60, clock: () => 0);

        Assert.Null(await store.AuthenticateAsync("bad", Key));
        Assert.Null(await store.AuthenticateAsync("bad", Key)); // a bad DSN must not hit the DB every time
        Assert.Equal(1, inner.Calls);
    }

    // Ingest authenticates before it rate-limits, so this is the first thing an unauthenticated caller
    // reaches. A credential nobody could have minted must cost neither a query nor an entry, which is
    // also what keeps a cached miss small: a header is otherwise as long as the request limit allows.
    [Theory]
    [InlineData("")]
    [InlineData("devkey")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF")] // right length, wrong case for what we mint
    [InlineData("0123456789abcdef0123456789abcde")] // one short
    [InlineData("0123456789abcdef0123456789abcdefa")] // one long
    public async Task AKeyNobodyCouldHaveMintedNeverReachesTheStore(string publicKey)
    {
        var inner = new CountingStore(new Project("1", Tier.Free));
        var store = new CachingProjectStore(inner, ttlSeconds: 60, clock: () => 0);

        Assert.Null(await store.AuthenticateAsync("1", publicKey));
        Assert.Equal(0, inner.Calls);
    }

    // The other half of a cache key. It arrives as a URL segment, so it is otherwise as long as the
    // server's request line allows, and an entry count is a memory bound only when an entry cannot be
    // arbitrarily heavy. A minted DSN carries a UUID here.
    [Fact]
    public async Task AProjectIdTooLongToHaveBeenMintedNeverReachesTheStore()
    {
        var inner = new CountingStore(new Project("1", Tier.Free));
        var store = new CachingProjectStore(inner, ttlSeconds: 60, clock: () => 0);

        Assert.Null(await store.AuthenticateAsync(new string('a', 8192), Key));
        Assert.Equal(0, inner.Calls);
        // The spelling a DSN actually carries is nowhere near the limit.
        Assert.NotNull(await store.AuthenticateAsync(Guid.NewGuid().ToString("B"), Key));
    }

    // The property the two dictionaries buy. A caller presenting well-formed credentials nobody issued
    // still reaches Postgres once each, which is what the rate limiter behind this is for; what it must
    // not do is evict the tenant that is actually sending events, because that turns a memory attack
    // into a database one on the path whose outage loses data rather than blocking a page.
    [Fact]
    public async Task AFloodOfStrangeCredentialsDoesNotEvictAWorkingOne()
    {
        var inner = new OneKeyStore(Key);
        var store = new CachingProjectStore(inner, ttlSeconds: 60, clock: () => 0);

        Assert.NotNull(await store.AuthenticateAsync("1", Key));
        var callsAfterWarmup = inner.Calls;

        for (var i = 0; i < 5_000; i++)
        {
            Assert.Null(await store.AuthenticateAsync("1", DsnKeyGenerator.NewPublicKey()));
        }

        Assert.NotNull(await store.AuthenticateAsync("1", Key));
        Assert.Equal(callsAfterWarmup + 5_000, inner.Calls); // the working key was still served from cache
    }

    // A working credential bounds the other dictionary, and it takes only one. A project id reaches the
    // relay as the caller spelled it, and Guid.TryParse accepts five formats and ignores case, so a
    // single valid DSN can be written as a great many strings that all resolve to the same row: the
    // store keeps answering, and every answer used to be a permanent entry. The fake here resolves on
    // the key alone, which is what Postgres does across spellings of one id.
    [Fact]
    public async Task OneWorkingCredentialSpelledManyWaysIsNotRememberedForEver()
    {
        // Comfortably past the hit cap. Raising that cap means raising this with it: the test asks
        // whether the cache ever forgets, and it cannot ask that without first filling it.
        const int pastTheCap = 20_000;
        var inner = new OneKeyStore(Key);
        var store = new CachingProjectStore(inner, ttlSeconds: 60, clock: () => 0);
        var first = Guid.NewGuid().ToString();

        Assert.NotNull(await store.AuthenticateAsync(first, Key));
        for (var i = 0; i < pastTheCap; i++)
        {
            Assert.NotNull(await store.AuthenticateAsync(Guid.NewGuid().ToString(), Key));
        }

        var before = inner.Calls;
        Assert.NotNull(await store.AuthenticateAsync(first, Key));
        Assert.Equal(before + 1, inner.Calls);
    }

    // The other half of the same property: the misses themselves are not kept for ever. Nothing can
    // observe the bucket directly, so this reads it the way the relay would: an early miss that has
    // been dropped costs one more query, and one that was kept costs none.
    [Fact]
    public async Task AFloodOfStrangeCredentialsIsNotRememberedForEver()
    {
        var inner = new CountingStore(result: null);
        var store = new CachingProjectStore(inner, ttlSeconds: 60, clock: () => 0);
        var first = DsnKeyGenerator.NewPublicKey();

        Assert.Null(await store.AuthenticateAsync("1", first));
        for (var i = 0; i < 2_000; i++)
        {
            Assert.Null(await store.AuthenticateAsync("1", DsnKeyGenerator.NewPublicKey()));
        }

        var before = inner.Calls;
        Assert.Null(await store.AuthenticateAsync("1", first));
        Assert.Equal(before + 1, inner.Calls);
    }
}
