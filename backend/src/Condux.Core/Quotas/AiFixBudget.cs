using Condux.Core.Plans;

namespace Condux.Core.Quotas;

/// <summary>Reads the org's month-to-date Conductor spend in USD, priced from the persisted per-run token
/// usage (#120). Only platform-billed (priced) models count; a bring-your-own-key org's usage is on its
/// own provider account and is not priced, so it contributes nothing toward a cost cap.</summary>
public interface IAiFixSpend
{
    Task<decimal> MonthToDateUsdAsync(long orgId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default);
}

/// <summary>The cost-cap rule (#120 budgets): an optional per-org monthly ceiling on Conductor spend.
/// Because a run's own cost is unknown until it completes, the cap gates the <b>next</b> run once
/// already-incurred spend has reached it (concurrent in-flight runs can overshoot slightly — the same
/// trade-off the run-count reservation accepts).</summary>
public static class AiFixBudget
{
    /// <summary>True when a cap is configured and month-to-date spend has reached it. A null cap (the
    /// default) never blocks; a zero cap blocks all runs.</summary>
    public static bool IsOverCap(decimal? capUsd, decimal spentUsd) => capUsd is { } cap && spentUsd >= cap;

    /// <summary>The cap actually enforced for an org (ADR-0020/0027): the org's own
    /// <paramref name="orgOverrideUsd"/> if set, else the tier's default fix-compute ceiling
    /// (<see cref="Limits.FixComputeCapUsd"/>). This is what unifies the two purposes: platform-billed
    /// tiers (Team/Business) get a fair-use compute ceiling by default that ops can raise per org, while
    /// Enterprise/BYO has a null tier default so the org's own value is a pure customer budget (null =
    /// uncapped, their money). Null result never blocks.</summary>
    public static decimal? EffectiveCapUsd(decimal? orgOverrideUsd, Limits limits) =>
        orgOverrideUsd ?? limits.FixComputeCapUsd;
}
