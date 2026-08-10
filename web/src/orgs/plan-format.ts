// Org tier (Condux.Core.Plans.Tier: 0 Free, 1 Team, 2 Business, 3 Enterprise) -> a display name.
const PLAN_NAMES: Record<number, string> = {
  0: "Free",
  1: "Team",
  2: "Business",
  3: "Enterprise",
};

export function planName(tier: number): string {
  return PLAN_NAMES[tier] ?? "Free";
}

// Tier capabilities, mirroring Condux.Core.Plans.PlanCatalog (the backend is the source of truth; these
// gate UI only). BYO-key (own provider, own spend budget) is Enterprise only.
//
// There is deliberately no tierHasAiFixes here any more: every tier now includes a monthly Conductor
// allowance, so the question the old helper answered no longer has a false case. It is also why the
// allowance and ceiling are read from the usage endpoint rather than derived from the tier — a mirror
// of the catalog silently went stale once Free changed, hiding a Free org's own allowance from it.
export function tierIsByo(tier: number): boolean {
  return tier === 3; // Enterprise
}
