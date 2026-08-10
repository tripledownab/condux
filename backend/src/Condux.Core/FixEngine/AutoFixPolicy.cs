using Condux.Core.Events;
using Condux.Core.Plans;

namespace Condux.Core.FixEngine;

/// <summary>How an org triggers Conductor fixes (#101). Persisted in <c>orgs.ai_fix_mode</c>.</summary>
public enum AiFixMode
{
    /// <summary>A user clicks "Suggest fix" on an issue (the default).</summary>
    Manual = 0,

    /// <summary>The platform requests a fix automatically when a qualifying issue lands.</summary>
    Auto = 1,
}

/// <summary>The pure decision for whether a newly-grouped issue should auto-trigger a Conductor fix
/// (#101), clock- and I/O-free so it unit-tests without a database. The caller still atomically reserves
/// the monthly allowance (a stateful reservation, not a pure check) after this returns true.</summary>
public static class AutoFixPolicy
{
    /// <summary>Auto-fix fires only when the org opted in, the plan allows auto-fix, a repo is linked
    /// (a fix needs somewhere to open a PR), and the issue is at least an error — warnings/info would
    /// burn the allowance on low-value issues. The trigger (new issue or regression) is decided by the
    /// caller, matching the alert dispatch.
    /// <para>The plan gate is <see cref="Limits.AutoFix"/>, never the allowance: a tier can include
    /// Conductor runs while still requiring a human to ask for each one. Free is exactly that, so gating
    /// on "has an allowance" here would let a Free org auto-burn its month on one noisy project.</para>
    /// </summary>
    public static bool ShouldTrigger(AiFixMode mode, Tier tier, bool hasRepo, Level level) =>
        mode == AiFixMode.Auto
        && PlanCatalog.For(tier).AutoFix
        && hasRepo
        && level >= Level.Error;
}
