using Condux.Core.Events;
using Condux.Core.FixEngine;
using Condux.Core.Plans;
using Xunit;

namespace Condux.Core.Tests;

public class AutoFixPolicyTests
{
    [Fact]
    public void Fires_for_an_auto_org_on_a_paid_tier_with_a_repo_and_an_error()
    {
        Assert.True(AutoFixPolicy.ShouldTrigger(AiFixMode.Auto, Tier.Team, hasRepo: true, Level.Error));
        Assert.True(AutoFixPolicy.ShouldTrigger(AiFixMode.Auto, Tier.Team, hasRepo: true, Level.Fatal));
    }

    [Fact]
    public void Does_not_fire_in_manual_mode()
    {
        Assert.False(AutoFixPolicy.ShouldTrigger(AiFixMode.Manual, Tier.Team, hasRepo: true, Level.Error));
    }

    [Fact]
    public void Does_not_fire_on_a_tier_that_includes_fixes_but_not_auto_fix()
    {
        // Free carries a real monthly allowance, so the gate here has to be AutoFix rather than AiFixes.
        // The first assert is load-bearing: without it this test would still pass if someone took the
        // allowance away from Free, hiding the regression it exists to catch.
        Assert.InRange(PlanCatalog.For(Tier.Free).AiFixesPerMonth, 1, int.MaxValue);
        Assert.False(PlanCatalog.For(Tier.Free).AutoFix);
        Assert.False(AutoFixPolicy.ShouldTrigger(AiFixMode.Auto, Tier.Free, hasRepo: true, Level.Error));
    }

    [Fact]
    public void Does_not_fire_without_a_linked_repo()
    {
        // A fix needs somewhere to open a PR.
        Assert.False(AutoFixPolicy.ShouldTrigger(AiFixMode.Auto, Tier.Team, hasRepo: false, Level.Error));
    }

    [Theory]
    [InlineData(Level.Warning)]
    [InlineData(Level.Info)]
    [InlineData(Level.Debug)]
    public void Does_not_fire_below_error_level(Level level)
    {
        Assert.False(AutoFixPolicy.ShouldTrigger(AiFixMode.Auto, Tier.Team, hasRepo: true, level));
    }
}
