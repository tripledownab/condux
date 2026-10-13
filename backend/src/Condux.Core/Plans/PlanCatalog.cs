namespace Condux.Core.Plans;

/// <summary>A pricing/ingest tier.</summary>
public enum Tier
{
    Free,
    Team,
    Business,
    Enterprise,
}

/// <summary>
/// The limits a tier sets. The monthly event cap, the upload quota and the AI fix allowance and budget
/// are the org's; the ingest rate and burst apply to each project; retention is stamped on each event.
/// </summary>
public readonly record struct Limits(
    long MonthlyEvents,
    double RatePerSecond,
    long Burst,
    int RetentionDays,
    int AiFixesPerMonth,
    bool AutoFix,
    bool Sso,
    bool ByoKey,
    int AiFixesLifetime = 0,
    decimal? FixComputeCapUsd = null,
    bool SelfHostedRunner = false)
{
    /// <summary>How many bytes of uploaded source maps and debug files one org may keep, across all its
    /// projects; 0 means no cap. Required rather than defaulted, because a default of 0 would leave a
    /// tier added later uncapped by omission.</summary>
    public required long UploadBytes { get; init; }

    /// <summary>
    /// True when the tier uses custom/unlimited event volume (Enterprise), which
    /// the relay treats as "no monthly cap".
    /// </summary>
    public bool Unlimited => MonthlyEvents == 0;

    /// <summary>
    /// True when the tier's AI fixes are uncapped (Enterprise; same 0-means-no-cap convention as
    /// <see cref="MonthlyEvents"/>).
    /// <para>Note the consequence of that convention: there is no way to express a tier with no fixes at
    /// all, because 0 already means unlimited. That is deliberate — every tier includes an allowance
    /// (ADR-0035) — but a future zero-fix tier would need an explicit representation, not a 0 here.</para>
    /// </summary>
    public bool UnlimitedAiFixes => AiFixesPerMonth == 0;

    /// <summary>True when an org's uploaded artifacts are not capped (Enterprise; the same
    /// 0-means-no-cap convention as <see cref="MonthlyEvents"/>).</summary>
    public bool UnlimitedUploads => UploadBytes == 0;
}

/// <summary>
/// Default limits per tier. These drive both billing and the ingest-tiering
/// controls (the relay enforces the rate limit + quota). Numbers are starting
/// points to calibrate against real usage.
/// </summary>
public static class PlanCatalog
{
    /// <summary>
    /// Retention (days) applied when a tier's own value isn't available: the ingest fallback when an
    /// event carries no retention header (an older message), and the backfill for ClickHouse rows that
    /// predate per-tier retention. Matches the prior flat TTL. The single source for this default — the
    /// ClickHouse column default in migration 0002 repeats it, because SQL cannot reference this
    /// constant, and the two are held together by test (pinned by PlanCatalogExportTests).
    /// </summary>
    public const int DefaultRetentionDays = 90;

    // AiFixesPerMonth is the tier's included allowance of Conductor runs per org per calendar month
    // (failed runs do not count), where 0 = uncapped, only Enterprise, whose BYO keys put the model spend
    // on the org's own account (ADR-0017). Every tier has an allowance, so there is no separate
    // has-AI-fixes flag: a boolean with no false case only invites drift (ADR-0035).
    // AutoFix is the separate question of whether the tier may set ai_fix_mode = auto, and is deliberately
    // independent of the allowance: Free has a real monthly one but stays manual, so a Free org cannot
    // point auto-fix at a noisy project and burn the month's runs without a human choosing each one.
    // AiFixesLifetime is a one-time grant that never resets. No tier uses it today (Free moved to the
    // monthly allowance above), but the machinery is intact and a single value here revives it.
    // FixComputeCapUsd is the tier's DEFAULT monthly fix-compute ceiling in USD (ADR-0020): for the
    // platform-billed tiers it is a fair-use compute ceiling, so a run count alone can't run up unbounded
    // compute ("up to N fixes OR $X compute, whichever first"); an org's own ai_fix_cost_cap_usd overrides
    // it. Each platform-billed ceiling is sized at about $0.75 a run times the tier's run allowance, so a
    // typical run count reaches its allowance before its dollar ceiling: a ceiling that binds first refuses
    // runs the plan advertises. Enterprise leaves it null: they run on their own key (BYO) and set their
    // own budget (null = uncapped, their money). All values are still to calibrate against real usage.
    // UploadBytes is how much of the source maps and native debug files CI uploads one org may keep, across
    // its projects; past it the oldest are evicted, so an upload never fails on storage. 0 = no cap.
    private static readonly Dictionary<Tier, Limits> Catalog = new()
    {
        [Tier.Free] = new Limits(50_000, 10, 50, 30,
            AiFixesPerMonth: 3, AutoFix: false, Sso: false, ByoKey: false, FixComputeCapUsd: 2.25m)
        { UploadBytes = 1L << 30 },
        [Tier.Team] = new Limits(500_000, 100, 500, 90,
            AiFixesPerMonth: 25, AutoFix: true, Sso: false, ByoKey: false, FixComputeCapUsd: 19m,
            SelfHostedRunner: true)
        { UploadBytes = 10L << 30 },
        [Tier.Business] = new Limits(5_000_000, 1_000, 5_000, 90,
            AiFixesPerMonth: 100, AutoFix: true, Sso: true, ByoKey: false, FixComputeCapUsd: 75m,
            SelfHostedRunner: true)
        { UploadBytes = 50L << 30 },
        [Tier.Enterprise] = new Limits(0, 0, 0, 90,
            AiFixesPerMonth: 0, AutoFix: true, Sso: true, ByoKey: true, SelfHostedRunner: true)
        { UploadBytes = 0 },
    };

    /// <summary>Limits for a tier, falling back to Free for unknown tiers.</summary>
    public static Limits For(Tier tier) => Catalog.TryGetValue(tier, out var limits) ? limits : Catalog[FallbackTier];

    /// <summary>The tier whose limits apply to a stored tier this version does not know.</summary>
    public const Tier FallbackTier = Tier.Free;
}
