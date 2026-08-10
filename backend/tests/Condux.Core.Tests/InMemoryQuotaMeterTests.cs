using Condux.Core.Quotas;
using Xunit;

namespace Condux.Core.Tests;

public class InMemoryQuotaMeterTests
{
    [Fact]
    public async Task Admits_up_to_the_limit_then_rejects()
    {
        var meter = new InMemoryQuotaMeter();

        var first = await meter.TryConsumeAsync("p", 2);
        var second = await meter.TryConsumeAsync("p", 2);
        var third = await meter.TryConsumeAsync("p", 2);

        Assert.True(first.Allowed);
        Assert.Equal(1, first.Remaining);
        Assert.True(second.Allowed);
        Assert.Equal(0, second.Remaining);
        Assert.False(third.Allowed);
        Assert.Equal(2, third.Used); // a rejected event does not grow usage past the limit
    }

    [Fact]
    public async Task Zero_limit_is_unlimited()
    {
        var decision = await new InMemoryQuotaMeter().TryConsumeAsync("p", 0);

        Assert.True(decision.Allowed);
        Assert.Equal(long.MaxValue, decision.Remaining);
    }

    [Fact]
    public async Task Quota_resets_when_the_month_rolls_over()
    {
        var clock = new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero);
        var meter = new InMemoryQuotaMeter(() => clock);

        Assert.True((await meter.TryConsumeAsync("p", 1)).Allowed);
        Assert.False((await meter.TryConsumeAsync("p", 1)).Allowed); // January exhausted

        clock = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.True((await meter.TryConsumeAsync("p", 1)).Allowed); // February starts fresh
    }

    [Fact]
    public async Task Quotas_are_tracked_per_key()
    {
        var meter = new InMemoryQuotaMeter();

        Assert.True((await meter.TryConsumeAsync("a", 1)).Allowed);
        Assert.False((await meter.TryConsumeAsync("a", 1)).Allowed);
        Assert.True((await meter.TryConsumeAsync("b", 1)).Allowed); // a different project has its own budget
    }
}
