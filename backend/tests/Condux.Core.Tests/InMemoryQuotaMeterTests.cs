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

    [Fact]
    public async Task A_batch_consumes_its_whole_count_in_one_call()
    {
        var meter = new InMemoryQuotaMeter();

        var decision = await meter.TryConsumeAsync("p", 10, count: 4);

        Assert.Equal(4, decision.Admitted);
        Assert.Equal(4, decision.Used); // the counter moved by the batch, not by one
        Assert.Equal(6, decision.Remaining);
    }

    // Refusing the whole batch would lose the last events of the month to whatever happened to arrive in a
    // group larger than the room left, so what fits is admitted and the caller is told how much that was.
    [Fact]
    public async Task A_batch_that_straddles_the_limit_admits_only_what_fits()
    {
        var meter = new InMemoryQuotaMeter();
        await meter.TryConsumeAsync("p", 10, count: 8);

        var decision = await meter.TryConsumeAsync("p", 10, count: 5);

        Assert.Equal(2, decision.Admitted); // 2 of the 5 asked for
        Assert.Equal(10, decision.Used); // and usage stops exactly at the limit
        Assert.Equal(0, decision.Remaining);
        Assert.False((await meter.TryConsumeAsync("p", 10, count: 5)).Allowed);
    }

    [Fact]
    public async Task An_unlimited_tier_admits_the_whole_batch()
    {
        var decision = await new InMemoryQuotaMeter().TryConsumeAsync("p", 0, count: 7);

        Assert.Equal(7, decision.Admitted);
        Assert.Equal(long.MaxValue, decision.Remaining);
    }

    // A negative count would hand quota back to the project, and asking for nothing has no decision to
    // return. Both are caller bugs rather than inputs, so the meter refuses to invent an answer.
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task A_count_of_zero_or_less_is_refused_rather_than_answered(long count)
    {
        var meter = new InMemoryQuotaMeter();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await meter.TryConsumeAsync("p", 10, count));
        Assert.Equal(1, (await meter.TryConsumeAsync("p", 10)).Used); // the counter never moved
    }

    // A caller that takes a batch and then cannot store all of it has spent the remainder on nothing, and
    // taking cannot be undone any other way.
    [Fact]
    public async Task A_refund_gives_back_what_was_taken_and_not_used()
    {
        var meter = new InMemoryQuotaMeter();
        await meter.TryConsumeAsync("p", 10, count: 6);

        await meter.RefundAsync("p", 4);

        var after = await meter.TryConsumeAsync("p", 10, count: 1);
        Assert.Equal(3, after.Used); // 6 taken, 4 given back, 1 taken again
    }

    // The floor is what stops a duplicate or late refund manufacturing quota out of nothing.
    [Fact]
    public async Task A_refund_larger_than_the_month_used_stops_at_zero()
    {
        var meter = new InMemoryQuotaMeter();
        await meter.TryConsumeAsync("p", 10, count: 2);

        await meter.RefundAsync("p", 50);

        Assert.Equal(1, (await meter.TryConsumeAsync("p", 10)).Used); // zero, then the one just taken
    }

    // Creating a counter to subtract from would write a negative that the next consume reads as free room.
    [Fact]
    public async Task A_refund_against_a_month_with_no_counter_creates_nothing()
    {
        var meter = new InMemoryQuotaMeter();

        await meter.RefundAsync("never-used", 5);

        Assert.Equal(1, (await meter.TryConsumeAsync("never-used", 10)).Used);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task A_refund_of_zero_or_less_is_refused(long count)
    {
        var meter = new InMemoryQuotaMeter();
        await meter.TryConsumeAsync("p", 10, count: 5);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await meter.RefundAsync("p", count));
        Assert.Equal(6, (await meter.TryConsumeAsync("p", 10)).Used); // a negative refund never took more
    }
}
