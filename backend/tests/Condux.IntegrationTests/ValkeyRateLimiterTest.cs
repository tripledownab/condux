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
