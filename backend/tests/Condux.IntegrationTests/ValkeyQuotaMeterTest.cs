using Condux.Storage.Quotas;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using StackExchange.Redis;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// Proves the Valkey-backed monthly quota meter shares one counter across replicas (the point of a
/// distributed meter), counts only admitted events, admits what fits of a batch, and resets on a month
/// boundary. Uses a real ephemeral Valkey container. Opt-in via <c>--filter Category=Integration</c>.
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

    // One OTLP export carries a record count the sender chose, so the batch is metered in a single script
    // call. The two properties that matter are both here: the counter moves by the batch, and a batch that
    // straddles the limit takes only the room left. Like the sequential test above, this shows the two
    // meters reading one counter, not that concurrent calls cannot over-admit. That comes from Valkey
    // running the script to completion, which nothing here exercises.
    [Fact]
    public async Task ABatchTakesWhatFits_AndTheCounterMovesByTheBatch()
    {
        var replicaA = new ValkeyQuotaMeter(_redis);
        var replicaB = new ValkeyQuotaMeter(_redis);
        var key = "batch-" + Guid.NewGuid().ToString("N");

        var first = await replicaA.TryConsumeAsync(key, 10, count: 6);
        Assert.Equal(6, first.Admitted);
        Assert.Equal(6, first.Used);
        Assert.Equal(4, first.Remaining);

        // The other replica sees the same counter, so only the remaining 4 of its 7 are admitted.
        var second = await replicaB.TryConsumeAsync(key, 10, count: 7);
        Assert.Equal(4, second.Admitted);
        Assert.Equal(10, second.Used);
        Assert.Equal(0, second.Remaining);

        var third = await replicaA.TryConsumeAsync(key, 10, count: 3);
        Assert.Equal(0, third.Admitted);
        Assert.False(third.Allowed);
        Assert.Equal(10, third.Used); // a refused batch never grows usage past the limit
    }

    // A caller that takes a batch and then cannot store all of it has spent the remainder on nothing, and
    // taking cannot be undone any other way.
    [Fact]
    public async Task ARefundGivesBackWhatWasTakenAndNotUsed()
    {
        var meter = new ValkeyQuotaMeter(_redis);
        var key = "refund-" + Guid.NewGuid().ToString("N");
        await meter.TryConsumeAsync(key, 10, count: 6);

        await meter.RefundAsync(key, 4);

        Assert.Equal(3, (await meter.TryConsumeAsync(key, 10)).Used); // 6 taken, 4 back, 1 taken again
    }

    // The clamp has to live inside the script: DECRBY on its own is atomic but unbounded, and clamping
    // after reading is a read-then-write two callers can interleave. Without it a duplicate or late refund
    // drives the counter negative, which the next consume reads as free room it can spend.
    [Fact]
    public async Task ARefundLargerThanTheMonthUsedStopsAtZero()
    {
        var meter = new ValkeyQuotaMeter(_redis);
        var key = "floor-" + Guid.NewGuid().ToString("N");
        await meter.TryConsumeAsync(key, 10, count: 2);

        await meter.RefundAsync(key, 50);

        Assert.Equal(1, (await meter.TryConsumeAsync(key, 10)).Used);
    }

    [Fact]
    public async Task ARefundAgainstAMonthWithNoCounterCreatesNothing()
    {
        var meter = new ValkeyQuotaMeter(_redis);
        var key = "absent-" + Guid.NewGuid().ToString("N");

        await meter.RefundAsync(key, 5);

        Assert.Equal(1, (await meter.TryConsumeAsync(key, 10)).Used);
    }

    // The same refusal InMemoryQuotaMeterTests asserts, on the other implementation: the precondition lives
    // once in IQuotaMeter.RequirePositiveCount, and this is what holds the two meters to it. A negative
    // count matters more here than in memory, because this one would INCRBY by it and hand quota back.
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ACountOfZeroOrLessIsRefusedRatherThanAnswered(long count)
    {
        var meter = new ValkeyQuotaMeter(_redis);
        var key = "guard-" + Guid.NewGuid().ToString("N");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await meter.TryConsumeAsync(key, 10, count));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await meter.RefundAsync(key, count));
        Assert.Equal(1, (await meter.TryConsumeAsync(key, 10)).Used); // the counter never moved
    }
}
