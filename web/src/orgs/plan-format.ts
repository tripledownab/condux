import { PLANS, planFor } from "@condux/plans";

// Tier names and capabilities come from @condux/plans, generated from Condux.Core.Plans.PlanCatalog —
// the code that enforces them. Nothing here restates a limit.
//
// The reason this file no longer hardcodes anything: a mirror of the catalog silently went stale when
// Free changed, hiding a Free org's own allowance from it, and the same shape sold Business a
// bring-your-own key the API refuses. Both were invisible because nothing compared the copy to the
// source. A backend test now fails when the generated facts drift from the catalog.
//
// The per-org allowance and ceiling still come from the usage endpoint rather than from here: those are
// live counters, not tier facts.
export function planName(tier: number): string {
  return Object.entries(PLANS).find(([, plan]) => plan.tier === tier)?.[0] ?? "Free";
}

/** Whether the tier may bring its own LLM key, which is what makes the cost cap the customer's budget. */
export function tierIsByo(tier: number): boolean {
  return planFor(tier).byoKey;
}

/** Whether the tier may run the Conductor on its own machines (ADR-0033). The API refuses the switch
 * below this, so the UI disables the option instead of inviting a click that cannot succeed. */
export function tierHasSelfHostedRunner(tier: number): boolean {
  return planFor(tier).selfHostedRunner;
}
