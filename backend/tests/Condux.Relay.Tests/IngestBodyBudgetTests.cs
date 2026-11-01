using Condux.Relay.Ingest;
using Xunit;

namespace Condux.Relay.Tests;

/// <summary>How a sender's share and its organisation's ceiling divide the relay's body budget, and how
/// they are sized and given back.</summary>
public class IngestBodyBudgetTests
{
    [Fact]
    public void A_sender_reads_within_its_share_its_organisations_ceiling_and_the_budget()
    {
        var budget = new IngestBodyBudget(150, 100, 60);
        using var a = budget.LeaseFor("org:1", "198.51.100.1");
        using var b = budget.LeaseFor("org:1", "198.51.100.2");
        using var other = budget.LeaseFor("org:2", "198.51.100.1");

        Assert.True(a.Budget.TryReserve(60));
        Assert.False(a.Budget.TryReserve(1));      // its share is full
        Assert.False(b.Budget.TryReserve(41));     // 40 of the organisation's ceiling is left
        Assert.True(b.Budget.TryReserve(40));
        Assert.False(other.Budget.TryReserve(51)); // 50 of the budget is left
        Assert.True(other.Budget.TryReserve(50));
    }

    [Fact]
    public void A_charge_refused_further_up_is_kept_by_nothing_below()
    {
        var budget = new IngestBodyBudget(10, 100, 60);
        using var lease = budget.LeaseFor("org:1", "198.51.100.1");

        Assert.False(lease.Budget.TryReserve(20));
        Assert.True(lease.Budget.TryReserve(10));
    }

    [Fact]
    public void Readers_from_one_address_share_one_share_until_the_last_is_done()
    {
        var budget = new IngestBodyBudget(1000, 100, 60);
        var first = budget.LeaseFor("org:1", "198.51.100.1");
        var second = budget.LeaseFor("org:1", "198.51.100.1");

        Assert.True(first.Budget.TryReserve(30));
        Assert.True(second.Budget.TryReserve(30));
        Assert.False(second.Budget.TryReserve(1));

        // The first reader is done, twice over, while the second still holds its 30: a reader arriving now
        // must find the same share, not a fresh one beside it.
        first.Budget.Release(30);
        first.Dispose();
        first.Dispose();
        using (var third = budget.LeaseFor("org:1", "198.51.100.1"))
        {
            Assert.False(third.Budget.TryReserve(31));
        }

        second.Budget.Release(30);
        second.Dispose();
        Assert.Equal(0, budget.SharesHeld);
        using var again = budget.LeaseFor("org:1", "198.51.100.1");
        Assert.True(again.Budget.TryReserve(60));
    }

    // A reader that has not charged anything yet still keeps its share and its ceiling in place.
    [Fact]
    public void A_share_stays_while_a_reader_that_has_charged_nothing_remains()
    {
        var budget = new IngestBodyBudget(1000, 100, 60);
        var first = budget.LeaseFor("org:1", "198.51.100.1");
        using var second = budget.LeaseFor("org:1", "198.51.100.1");

        first.Dispose();
        Assert.True(second.Budget.TryReserve(60));

        using var third = budget.LeaseFor("org:1", "198.51.100.1");
        Assert.False(third.Budget.TryReserve(1));
        using var elsewhere = budget.LeaseFor("org:1", "198.51.100.2");
        Assert.False(elsewhere.Budget.TryReserve(41));
    }

    // Given back in the wrong order, a share still holding bytes stays, and so does its ceiling, so the
    // sender cannot read into a fresh one beside it.
    [Fact]
    public void A_share_still_charged_when_its_last_reader_leaves_is_kept()
    {
        var budget = new IngestBodyBudget(1000, 100, 60);
        var lease = budget.LeaseFor("org:1", "198.51.100.1");
        Assert.True(lease.Budget.TryReserve(30));

        lease.Dispose();

        using var next = budget.LeaseFor("org:1", "198.51.100.1");
        Assert.False(next.Budget.TryReserve(31));
        using var elsewhere = budget.LeaseFor("org:1", "198.51.100.2");
        Assert.False(elsewhere.Budget.TryReserve(71));
    }

    // A share holds one body at the cap at its peak (about twice the cap while it arrives), a ceiling two
    // such shares, and the budget four ceilings; one more body at its peak finds no room.
    [Theory]
    [InlineData(20L * 1024 * 1024)]
    [InlineData(200L * 1024 * 1024)]
    public void The_budget_for_a_cap_admits_bodies_at_the_cap_from_two_addresses_of_four_organisations(long cap)
    {
        var budget = IngestBodyBudget.ForCap(cap, long.MaxValue);
        var peak = 2 * cap + 64 * 1024;
        var leases = Enumerable.Range(0, 8).Select(i => budget.LeaseFor($"org:{i / 2}", $"198.51.100.{i}")).ToList();

        Assert.All(leases, lease => Assert.True(lease.Budget.TryReserve(peak)));
        using var third = budget.LeaseFor("org:0", "198.51.100.99");
        Assert.False(third.Budget.TryReserve(peak));
        using var fifth = budget.LeaseFor("org:4", "198.51.100.99");
        Assert.False(fifth.Budget.TryReserve(peak));
        leases.ForEach(lease => lease.Dispose());
    }

    // In a container the runtime may use less than the budget would take (a 384 MiB limit leaves it 288),
    // and a budget past that is met by the process running out of memory before any body is refused.
    [Fact]
    public void The_budget_never_takes_more_than_half_the_memory_the_runtime_may_use()
    {
        var budget = IngestBodyBudget.ForCap(20 * 1024 * 1024, 288L * 1024 * 1024);

        Assert.True(budget.TryReserve(144L * 1024 * 1024));
        Assert.False(budget.TryReserve(1));
    }

    // A small cap does not shrink the budget below its floor; it only makes the shares smaller.
    [Fact]
    public void The_budget_is_at_least_256_MiB_where_memory_allows()
    {
        var budget = IngestBodyBudget.ForCap(1024 * 1024, long.MaxValue);

        Assert.True(budget.TryReserve(256L * 1024 * 1024));
        Assert.False(budget.TryReserve(1));
    }
}
