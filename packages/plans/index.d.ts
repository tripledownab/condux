/**
 * Plan facts generated from `Condux.Core.Plans.PlanCatalog`, the code that enforces them.
 *
 * Limits and capability flags only. Prices live in Stripe and the pricing card (ADR-0026 keeps the
 * catalog free of commercial terms), and taglines and feature phrasing stay hand-written.
 *
 * Do not edit `plans.json` by hand: a backend test compares it to the catalog and fails on drift.
 */
export interface PlanFacts {
  /** The numeric `Tier` value, matching what the API sends for an org's tier. */
  tier: number;
  monthlyEvents: number;
  /** Events are uncapped for this tier (the catalog's 0-means-no-cap convention, resolved). */
  unlimitedEvents: boolean;
  ratePerSecond: number;
  burst: number;
  retentionDays: number;
  aiFixesPerMonth: number;
  /** Conductor runs are uncapped for this tier. */
  unlimitedAiFixes: boolean;
  /** Whether the tier may set `ai_fix_mode = auto`. Deliberately independent of the allowance. */
  autoFix: boolean;
  sso: boolean;
  /** Whether the org may bring its own LLM key. */
  byoKey: boolean;
  /** Whether the org may run the Conductor on its own machines instead of ours. */
  selfHostedRunner: boolean;
  /**
   * The tier's default monthly fix-compute ceiling in USD; null means uncapped. It does not apply to a
   * run executed on the org's own runner, which is billed to their model account rather than ours.
   */
  fixComputeCapUsd: number | null;
}

export declare const PLANS: Record<string, PlanFacts>;

/** The enforced limits for a tier, by its numeric `Tier` value. */
export declare function planFor(tier: number): PlanFacts;
