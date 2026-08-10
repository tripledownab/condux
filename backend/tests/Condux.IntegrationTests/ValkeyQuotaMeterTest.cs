using Condux.Storage.Quotas;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using StackExchange.Redis;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// Proves the Valkey-backed monthly quota meter shares one counter across replicas (the point of a
/// distributed meter), counts only admitted events, and resets on a month boundary. Uses a real ephemeral
/// Valkey container. Opt-in via <c>--filter Category=Integration</c>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ValkeyQuotaMeterTest : IAsyncLifetime
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
        _redis = await ConnectionMultiplexer.ConnectAsync(
            $"{_valkey.Hostname}:{_valkey.GetMappedPublicPort(6379)}");
    }

    public async Task DisposeAsync()
    {
        await _redis.DisposeAsync();
        await _valkey.DisposeAsync();
    }

    [Fact]
    public async Task MonthlyBudgetIsSharedAcrossInstances_AndOnlyAdmittedEventsCount()
    {
        // Two meters on the same Valkey stand in for two relay replicas.
        var replicaA = new ValkeyQuotaMeter(_redis);
        var replicaB = new ValkeyQuotaMeter(_redis);
        var key = "proj-" + Guid.NewGuid().ToString("N");

        // Limit 3, shared across replicas: the first three admits (spread across A/B) pass.
        Assert.True((await replicaA.TryConsumeAsync(key, 3)).Allowed);
        Assert.True((await replicaB.TryConsumeAsync(key, 3)).Allowed);
        var third = await replicaA.TryConsumeAsync(key, 3);
        Assert.True(third.Allowed);
        Assert.Equal(0, third.Remaining);

        // The 4th is denied, and the denial does not grow usage past the limit.
        var denied = await replicaB.TryConsumeAsync(key, 3);
        Assert.False(denied.Allowed);
        Assert.Equal(3, denied.Used);
        Assert.False((await replicaA.TryConsumeAsync(key, 3)).Allowed); // still 3, not 4
    }

    [Fact]
    public async Task SeparateKeysHaveSeparateBudgets()
    {
        var meter = new ValkeyQuotaMeter(_redis);
        var a = "a-" + Guid.NewGuid().ToString("N");
        var b = "b-" + Guid.NewGuid().ToString("N");

        Assert.True((await meter.TryConsumeAsync(a, 1)).Allowed);
        Assert.True((await meter.TryConsumeAsync(b, 1)).Allowed); // its own counter
        Assert.False((await meter.TryConsumeAsync(a, 1)).Allowed); // a exhausted
    }

    [Fact]
    public async Task QuotaResetsWhenTheMonthRollsOver()
    {
        var clock = new DateTimeOffset(2026, 1, 20, 0, 0, 0, TimeSpan.Zero);
        var meter = new ValkeyQuotaMeter(_redis, clock: () => clock);
        var key = "roll-" + Guid.NewGuid().ToString("N");

        Assert.True((await meter.TryConsumeAsync(key, 1)).Allowed);
        Assert.False((await meter.TryConsumeAsync(key, 1)).Allowed); // January drained

        clock = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.True((await meter.TryConsumeAsync(key, 1)).Allowed); // February is a fresh key
    }

    [Fact]
    public async Task ZeroLimitIsUnlimited()
    {
        var meter = new ValkeyQuotaMeter(_redis);
        var key = "ent-" + Guid.NewGuid().ToString("N");
        for (var i = 0; i < 100; i++)
        {
            Assert.True((await meter.TryConsumeAsync(key, 0)).Allowed);
        }
    }
}
