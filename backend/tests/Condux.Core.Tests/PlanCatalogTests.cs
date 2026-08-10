using Condux.Core.Plans;
using Xunit;

namespace Condux.Core.Tests;

public class PlanCatalogTests
{
    [Fact]
    public void TeamHas90DayRetention() =>
        Assert.Equal(90, PlanCatalog.For(Tier.Team).RetentionDays);

    [Fact]
    public void BusinessIncludesSso() =>
        Assert.True(PlanCatalog.For(Tier.Business).Sso);

    [Fact]
    public void EnterpriseRequiresByoKeyAndIsUnlimited()
    {
        var ent = PlanCatalog.For(Tier.Enterprise);
        Assert.True(ent.ByoKey);
        Assert.True(ent.Unlimited);
    }

    [Fact]
    public void FreeIsNotUnlimited() =>
        Assert.False(PlanCatalog.For(Tier.Free).Unlimited);

    [Fact]
    public void AiFixAllowancesScaleWithTheLadder()
    {
        // Every self-serve tier carries a finite monthly allowance that grows up the ladder; Enterprise is
        // the only uncapped one (BYO keys put the model spend on the org's own account, ADR-0017).
        var free = PlanCatalog.For(Tier.Free);
        var team = PlanCatalog.For(Tier.Team);
        var business = PlanCatalog.For(Tier.Business);

        Assert.InRange(free.AiFixesPerMonth, 1, int.MaxValue);
        Assert.True(team.AiFixesPerMonth > free.AiFixesPerMonth);
        Assert.True(business.AiFixesPerMonth > team.AiFixesPerMonth);
        Assert.True(PlanCatalog.For(Tier.Enterprise).UnlimitedAiFixes);

        // 0 means uncapped, which makes a zero allowance actively dangerous on a metered tier: none of
        // these may read as unlimited.
        Assert.False(free.UnlimitedAiFixes);
        Assert.False(team.UnlimitedAiFixes);
        Assert.False(business.UnlimitedAiFixes);
    }

    [Fact]
    public void FreeIncludesFixesButOnlyWhenAHumanAsks()
    {
        // Free runs the Conductor on its monthly allowance, but may not enable auto mode: unattended, one
        // noisy project would spend the whole month on its own.
        var free = PlanCatalog.For(Tier.Free);
        Assert.InRange(free.AiFixesPerMonth, 1, int.MaxValue);
        Assert.False(free.AutoFix);
    }

    [Fact]
    public void PaidTiersMayAutoFix()
    {
        Assert.True(PlanCatalog.For(Tier.Team).AutoFix);
        Assert.True(PlanCatalog.For(Tier.Business).AutoFix);
        Assert.True(PlanCatalog.For(Tier.Enterprise).AutoFix);
    }

    [Fact]
    public void EveryPlatformBilledTierCapsFixCompute()
    {
        // A run count alone does not bound spend, since one run's cost depends on the repo. Each tier
        // Condux pays the model bill for therefore carries a dollar ceiling too, growing with the plan.
        var free = PlanCatalog.For(Tier.Free);
        var team = PlanCatalog.For(Tier.Team);
        var business = PlanCatalog.For(Tier.Business);

        Assert.NotNull(free.FixComputeCapUsd);
        Assert.True(team.FixComputeCapUsd > free.FixComputeCapUsd);
        Assert.True(business.FixComputeCapUsd > team.FixComputeCapUsd);

        // Enterprise is BYO, so the budget is the org's own money and its own business.
        Assert.Null(PlanCatalog.For(Tier.Enterprise).FixComputeCapUsd);
    }
}
