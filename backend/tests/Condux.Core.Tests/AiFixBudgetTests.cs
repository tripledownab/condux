using Condux.Core.Plans;
using Condux.Core.Quotas;
using Xunit;

namespace Condux.Core.Tests;

public class AiFixBudgetTests
{
    [Fact]
    public void EffectiveCap_OrgOverrideWins()
    {
        // An org's own cap overrides the tier default (either direction).
        Assert.Equal(5m, AiFixBudget.EffectiveCapUsd(orgOverrideUsd: 5m, PlanCatalog.For(Tier.Team)));
        Assert.Equal(200m, AiFixBudget.EffectiveCapUsd(orgOverrideUsd: 200m, PlanCatalog.For(Tier.Enterprise)));
    }

    [Fact]
    public void EffectiveCap_PlatformTiers_FallBackToPlanDefault()
    {
        // The platform-billed tiers carry a fair-use compute ceiling by default, applied when the org
        // sets no override of its own. A run count alone does not bound spend, because one run's cost
        // depends on the repo it runs against.
        Assert.NotNull(AiFixBudget.EffectiveCapUsd(orgOverrideUsd: null, PlanCatalog.For(Tier.Team)));
        Assert.NotNull(AiFixBudget.EffectiveCapUsd(orgOverrideUsd: null, PlanCatalog.For(Tier.Business)));
    }

    [Fact]
    public void EffectiveCap_Enterprise_HasNoDefault_SoNullOverrideIsUncapped()
    {
        // Enterprise/BYO self-budgets: no tier default, so an unset override means uncapped (their money).
        Assert.Null(AiFixBudget.EffectiveCapUsd(orgOverrideUsd: null, PlanCatalog.For(Tier.Enterprise)));
    }

    [Fact]
    public void NoCap_NeverBlocks()
    {
        Assert.False(AiFixBudget.IsOverCap(capUsd: null, spentUsd: 9999m));
    }

    [Fact]
    public void UnderCap_DoesNotBlock()
    {
        Assert.False(AiFixBudget.IsOverCap(capUsd: 50m, spentUsd: 49.99m));
    }

    [Fact]
    public void AtOrOverCap_Blocks()
    {
        // Reaching the cap exactly blocks the next run (>=), as does exceeding it.
        Assert.True(AiFixBudget.IsOverCap(capUsd: 50m, spentUsd: 50m));
        Assert.True(AiFixBudget.IsOverCap(capUsd: 50m, spentUsd: 50.01m));
    }

    [Fact]
    public void ZeroCap_BlocksEverything()
    {
        Assert.True(AiFixBudget.IsOverCap(capUsd: 0m, spentUsd: 0m));
    }
}
