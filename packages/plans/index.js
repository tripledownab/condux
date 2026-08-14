// Generated facts, re-exported so a consumer imports a module rather than reaching for a JSON path.
// Regenerate plans.json with: CONDUX_WRITE_PLANS=1 dotnet test (backend/tests/Condux.Core.Tests).
import plans from "./plans.json" with { type: "json" };

export const PLANS = plans;

/** The enforced limits for a tier, by its numeric Condux.Core.Plans.Tier value. */
export function planFor(tier) {
  return Object.values(plans).find((p) => p.tier === tier) ?? plans.Free;
}
