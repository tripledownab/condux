using Condux.Core.RateLimiting;
using Xunit;

namespace Condux.Core.Tests;

public class TokenBucketTests
{
    [Fact]
    public void ConsumesUntilEmptyThenDenies()
    {
        var state = new TokenBucket.State(Tokens: 2, UpdatedAt: 0);
        (state, var d1) = TokenBucket.Step(state, ratePerSecond: 1, burst: 2, nowSeconds: 0);
        (state, var d2) = TokenBucket.Step(state, ratePerSecond: 1, burst: 2, nowSeconds: 0);
        (_, var d3) = TokenBucket.Step(state, ratePerSecond: 1, burst: 2, nowSeconds: 0);

        Assert.True(d1.Allowed);
        Assert.True(d2.Allowed);
        Assert.False(d3.Allowed);
        Assert.True(d3.RetryAfterSeconds >= 1);
    }

    [Fact]
    public void RefillsProportionalToElapsedTime()
    {
        var state = new TokenBucket.State(Tokens: 1, UpdatedAt: 0);
        (state, _) = TokenBucket.Step(state, ratePerSecond: 10, burst: 5, nowSeconds: 0); // drains to 0
        (state, var denied) = TokenBucket.Step(state, ratePerSecond: 10, burst: 5, nowSeconds: 0);
        (_, var afterWait) = TokenBucket.Step(state, ratePerSecond: 10, burst: 5, nowSeconds: 0.2); // +2 tokens

        Assert.False(denied.Allowed);
        Assert.True(afterWait.Allowed);
    }

    [Fact]
    public void RefillIsCappedAtBurst()
    {
        var state = new TokenBucket.State(Tokens: 0, UpdatedAt: 0);
        // A long idle period must not let tokens exceed the burst capacity.
        (_, var d) = TokenBucket.Step(state, ratePerSecond: 1, burst: 3, nowSeconds: 10_000);
        Assert.True(d.Allowed);
        Assert.Equal(2, d.Remaining); // 3 capacity, minus the one just consumed
    }

    [Fact]
    public void ZeroRateIsUnlimited()
    {
        var state = new TokenBucket.State(Tokens: 0, UpdatedAt: 0);
        var (next, d) = TokenBucket.Step(state, ratePerSecond: 0, burst: 0, nowSeconds: 0);
        Assert.True(d.Allowed);
        Assert.Equal(state, next); // unlimited never mutates state
    }
}

public class InMemoryRateLimiterTests
{
    [Fact]
    public async Task BurstThenDeny()
    {
        var limiter = new InMemoryRateLimiter(clock: () => 0);
        for (var i = 0; i < 5; i++)
        {
            Assert.True((await limiter.CheckAsync("p1", ratePerSecond: 10, burst: 5)).Allowed);
        }

        var d = await limiter.CheckAsync("p1", ratePerSecond: 10, burst: 5);
        Assert.False(d.Allowed);
        Assert.True(d.RetryAfterSeconds >= 1);
    }

    [Fact]
    public async Task SeparateKeysHaveSeparateBudgets()
    {
        var limiter = new InMemoryRateLimiter(clock: () => 0);
        Assert.True((await limiter.CheckAsync("a", 1, 1)).Allowed);
        Assert.True((await limiter.CheckAsync("b", 1, 1)).Allowed); // own bucket
        Assert.False((await limiter.CheckAsync("a", 1, 1)).Allowed); // a is drained
    }

    [Fact]
    public async Task RefillsOverTime()
    {
        var now = 0.0;
        var limiter = new InMemoryRateLimiter(clock: () => now);
        Assert.True((await limiter.CheckAsync("p", 10, 1)).Allowed);
        Assert.False((await limiter.CheckAsync("p", 10, 1)).Allowed);
        now = 0.2; // ~2 tokens refill after 0.2s at 10/s
        Assert.True((await limiter.CheckAsync("p", 10, 1)).Allowed);
    }

    [Fact]
    public async Task ZeroRateIsUnlimited()
    {
        var limiter = new InMemoryRateLimiter(clock: () => 0);
        for (var i = 0; i < 1_000; i++)
        {
            Assert.True((await limiter.CheckAsync("ent", 0, 0)).Allowed);
        }
    }
}
