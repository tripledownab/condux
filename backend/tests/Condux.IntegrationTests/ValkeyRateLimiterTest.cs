using Condux.Core.RateLimiting;
using Condux.Storage.RateLimiting;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using StackExchange.Redis;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// Proves the Valkey-backed limiter enforces one budget across instances (the
/// point of a distributed limiter: many relay replicas, one shared quota). Uses a
/// real ephemeral Valkey container. Opt-in via <c>--filter Category=Integration</c>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ValkeyRateLimiterTest : IAsyncLifetime
{
    private readonly IContainer _valkey = new ContainerBuilder()
        .WithImage("valkey/valkey:8-alpine")
        .WithPortBinding(6379, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(6379))
        .Build();

    private ConnectionMultiplexer _redis = null!;

    public async Task InitializeAsync()
    {
        await _valkey.StartAsync();
        var endpoint = $"{_valkey.Hostname}:{_valkey.GetMappedPublicPort(6379)}";
        _redis = await ConnectionMultiplexer.ConnectAsync(endpoint);
    }

    public async Task DisposeAsync()
    {
        await _redis.DisposeAsync();
        await _valkey.DisposeAsync();
    }

    [Fact]
    public async Task BudgetIsSharedAcrossInstances()
    {
        // Two limiters on the same Valkey stand in for two relay replicas.
        var replicaA = new ValkeyRateLimiter(_redis);
        var replicaB = new ValkeyRateLimiter(_redis);
        var key = "proj-" + Guid.NewGuid().ToString("N");

        // burst = 3, low refill: the first three admits (spread across replicas) pass.
        Assert.True((await replicaA.CheckAsync(key, ratePerSecond: 1, burst: 3)).Allowed);
        Assert.True((await replicaB.CheckAsync(key, ratePerSecond: 1, burst: 3)).Allowed);
        Assert.True((await replicaA.CheckAsync(key, ratePerSecond: 1, burst: 3)).Allowed);

        // The 4th is denied because the budget is shared, with a positive Retry-After.
        var denied = await replicaB.CheckAsync(key, ratePerSecond: 1, burst: 3);
        Assert.False(denied.Allowed);
        Assert.True(denied.RetryAfterSeconds >= 1);
    }

    [Fact]
    public async Task SeparateKeysHaveSeparateBudgets()
    {
        var limiter = new ValkeyRateLimiter(_redis);
        var a = "a-" + Guid.NewGuid().ToString("N");
        var b = "b-" + Guid.NewGuid().ToString("N");

        Assert.True((await limiter.CheckAsync(a, ratePerSecond: 1, burst: 1)).Allowed);
        Assert.True((await limiter.CheckAsync(b, ratePerSecond: 1, burst: 1)).Allowed); // own bucket
        Assert.False((await limiter.CheckAsync(a, ratePerSecond: 1, burst: 1)).Allowed); // a drained
    }

    [Fact]
    public async Task The_lua_decides_exactly_what_the_shared_token_bucket_decides()
    {
        // The Lua script is a second implementation of TokenBucket.Step, in another language, which the
        // limiter's own comment claims agrees with it. Nothing compared them until this test, so the
        // claim rested on two people having read both.
        //
        // Refill is what makes them hard to compare, because the Lua reads the Valkey server clock and
        // cannot be given ours. Rather than race that clock, this reads back the instant the Lua wrote
        // and steps the C# bucket to exactly there, so the two are compared over identical inputs and
        // no amount of delay between iterations can move either answer.
        //
        // Assuming elapsed is zero instead was the first version, and it is wrong in a way worth
        // recording: retry-after is ceil((1 - tokens) / rate), whose granularity scales with the rate,
        // so it starts disagreeing after one second of real time at ANY rate. Lowering the rate widens
        // the tolerance of `allowed` and `remaining` and does nothing for the one that binds.
        const string prefix = "parity:";
        var limiter = new ValkeyRateLimiter(_redis, prefix);
        var key = "rl-" + Guid.NewGuid().ToString("N");
        const double rate = 1;
        const long burst = 5;

        TokenBucket.State? expected = null;
        for (var i = 0; i < burst + 2; i++)
        {
            var actual = await limiter.CheckAsync(key, rate, burst);

            // The `ts` the script just stored IS the instant it used, so the model gets the Lua's own
            // clock. On the first call the bucket did not exist, and the script starts it full at that
            // same instant, which is what the model is seeded with.
            var now = (double)await _redis.GetDatabase().HashGetAsync(prefix + key, "ts");
            expected ??= new TokenBucket.State(Tokens: burst, UpdatedAt: now);

            (expected, var want) = TokenBucket.Step(expected.Value, rate, burst, now);

            Assert.Equal(want.Allowed, actual.Allowed);
            Assert.Equal(want.Remaining, actual.Remaining);
            Assert.Equal(want.RetryAfterSeconds, actual.RetryAfterSeconds);
        }
    }

    [Fact]
    public async Task ZeroRateIsUnlimited()
    {
        var limiter = new ValkeyRateLimiter(_redis);
        var key = "ent-" + Guid.NewGuid().ToString("N");
        for (var i = 0; i < 100; i++)
        {
            Assert.True((await limiter.CheckAsync(key, ratePerSecond: 0, burst: 0)).Allowed);
        }
    }
}
