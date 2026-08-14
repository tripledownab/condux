using Condux.Core.FixEngine;
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
        Assert.Equal(5m, AiFixBudget.EffectiveCapUsd(5m, PlanCatalog.For(Tier.Team), FixExecution.Hosted));
        Assert.Equal(
            200m, AiFixBudget.EffectiveCapUsd(200m, PlanCatalog.For(Tier.Enterprise), FixExecution.Hosted));
    }

    [Fact]
    public void EffectiveCap_PlatformTiers_FallBackToPlanDefault()
    {
        // The platform-billed tiers carry a fair-use compute ceiling by default, applied when the org
        // sets no override of its own. A run count alone does not bound spend, because one run's cost
        // depends on the repo it runs against.
        Assert.NotNull(AiFixBudget.EffectiveCapUsd(null, PlanCatalog.For(Tier.Team), FixExecution.Hosted));
        Assert.NotNull(AiFixBudget.EffectiveCapUsd(null, PlanCatalog.For(Tier.Business), FixExecution.Hosted));
    }

    [Fact]
    public void EffectiveCap_Enterprise_HasNoDefault_SoNullOverrideIsUncapped()
    {
        // Enterprise/BYO self-budgets: no tier default, so an unset override means uncapped (their money).
        Assert.Null(AiFixBudget.EffectiveCapUsd(null, PlanCatalog.For(Tier.Enterprise), FixExecution.Hosted));
    }

    [Fact]
    public void EffectiveCap_OwnRunner_IsNotBoundByTheTierComputeCeiling()
    {
        // The fair-use ceiling exists because we pay for hosted model calls. A run on the org's own runner
        // is billed to their own model account, so enforcing it there would refuse a customer their own
        // money — and it would hit exactly the tiers that self-host to escape the ceiling.
        Assert.Null(AiFixBudget.EffectiveCapUsd(null, PlanCatalog.For(Tier.Team), FixExecution.Runner));
        Assert.Null(AiFixBudget.EffectiveCapUsd(null, PlanCatalog.For(Tier.Business), FixExecution.Runner));

        // Their own budget still binds: that one they set themselves.
        Assert.Equal(25m, AiFixBudget.EffectiveCapUsd(25m, PlanCatalog.For(Tier.Team), FixExecution.Runner));
    }

    [Fact]
    public void SelfHostedRunner_IsAPaidCapability_AndFreeIsExcluded()
    {
        // Gated at Team rather than Enterprise deliberately: the tiers with a fair-use compute ceiling are
        // the ones a runner helps, and it costs the platform nothing since the customer brings the compute
        // and the key. Free stays out to bound the support surface, not the spend.
        Assert.False(PlanCatalog.For(Tier.Free).SelfHostedRunner);
        Assert.True(PlanCatalog.For(Tier.Team).SelfHostedRunner);
        Assert.True(PlanCatalog.For(Tier.Business).SelfHostedRunner);
        Assert.True(PlanCatalog.For(Tier.Enterprise).SelfHostedRunner);
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
