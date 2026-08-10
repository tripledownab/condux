// Conductor fix-domain mappings (FixStatus enum -> catalog keys + colors) and error classification.
// Mirrors issue-format.ts; generic helpers live in @/src/lib.
import { ConduxApiError } from "@/src/api/fetcher";

// Lifecycle of a fix run. Values match backend Condux.Core.FixEngine.FixStatus and the wire FixStatus.
export enum FixStatus {
  Unspecified = 0,
  Pending = 1,
  Running = 2,
  Succeeded = 3,
  Failed = 4,
  Cancelled = 5,
}

export type FixStatusMeta = { key: string; className: string };

// FixStatus -> a label key under "issues.fix.status" plus a color. The label always travels with the
// color so state is never conveyed by color alone (accessibility).
const FIX_STATUS_META: Record<number, FixStatusMeta> = {
  [FixStatus.Unspecified]: { key: "unknown", className: "text-muted-foreground" },
  [FixStatus.Pending]: { key: "pending", className: "text-muted-foreground" },
  [FixStatus.Running]: { key: "running", className: "text-warning" },
  [FixStatus.Succeeded]: { key: "succeeded", className: "text-info" },
  [FixStatus.Failed]: { key: "failed", className: "text-error" },
  [FixStatus.Cancelled]: { key: "cancelled", className: "text-muted-foreground" },
};

export function fixStatusMeta(status: number): FixStatusMeta {
  // An unexpected value surfaces as "unknown" rather than masquerading as a real state.
  return FIX_STATUS_META[status] ?? FIX_STATUS_META[FixStatus.Unspecified];
}

// A run the Conductor is still working on, so the list should keep polling until it settles.
export function isFixInFlight(status: number): boolean {
  return status === FixStatus.Pending || status === FixStatus.Running;
}

// Post-merge verification of a fix run. Values match backend Condux.Core.FixEngine.VerifyStatus:
// once the draft PR merges the issue is watched against its event stats; a silent window
// auto-resolves it with evidence, any occurrence marks the fix as not holding.
export enum VerifyStatus {
  None = 0,
  Watching = 1,
  Held = 2,
  DidNotHold = 3,
}

// VerifyStatus -> a label key under "issues.fix.verify" plus a color. None renders nothing (the
// run's own status already tells the story until the PR merges).
const VERIFY_STATUS_META: Record<number, FixStatusMeta> = {
  [VerifyStatus.Watching]: { key: "watching", className: "text-warning" },
  [VerifyStatus.Held]: { key: "held", className: "text-success" },
  [VerifyStatus.DidNotHold]: { key: "didNotHold", className: "text-error" },
};

export function verifyStatusMeta(status: number | undefined): FixStatusMeta | null {
  return status === undefined ? null : (VERIFY_STATUS_META[status] ?? null);
}

// The expected control-plane rejection codes -> label keys under "issues.fix".
//
// ai_fix_cost_cap_exceeded matters as much as the allowance: RequestFix checks the fair-use compute
// ceiling BEFORE reserving a run, so an org with runs left can still be refused. Leaving it unmapped fell
// through to the generic "try again", which is untrue — retrying cannot succeed until the month rolls.
// There is deliberately no ai_fixes_requires_upgrade here: every tier now includes Conductor runs
// (ADR-0035), so RequestFix never returns it; it survives only as the auto-mode refusal on PATCH /orgs.
const FIX_ERROR_KEYS: Record<string, string> = {
  no_repo_linked: "noRepo",
  ai_fix_quota_exceeded: "quotaExceeded",
  ai_fix_cost_cap_exceeded: "costCapExceeded",
};

// Map a failed trigger to a label key under "issues.fix": the expected control-plane rejections (no
// repo linked yet, the monthly allowance spent, the compute ceiling reached, or too low a role) get a
// specific message; else generic.
export function fixErrorKey(error: unknown): string {
  if (error instanceof ConduxApiError) {
    const codeKey = error.code === undefined ? undefined : FIX_ERROR_KEYS[error.code];
    if (codeKey !== undefined) {
      return codeKey;
    }
    if (error.status === 403) {
      return "forbidden";
    }
  }
  return "failed";
}
