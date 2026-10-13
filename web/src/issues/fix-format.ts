// Conductor fix-domain mappings (FixStatus enum -> catalog keys + colors) and error classification.
// Mirrors issue-format.ts; generic helpers live in @/src/lib.
import { lookUpCodeKey, readErrorCode, readErrorStatus } from "@/src/lib/api-errors";

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

// Post-merge verification of a fix run or a CVE bump. Values match backend
// Condux.Core.FixEngine.VerifyStatus, compared with it by fix-format.test.ts: once the draft PR merges
// the issue (or the bumped package) is watched; a clean window holds, a recurrence or a still-running
// vulnerable version does not, and a bump the inventory cannot settle cannot be verified.
export enum VerifyStatus {
  None = 0,
  Watching = 1,
  Held = 2,
  DidNotHold = 3,
  CannotVerify = 4,
}

// VerifyStatus -> a label key under "issues.fix.verify" plus a color, with a CVE bump's own wording
// where an issue fix's would be wrong: a bump watches what runs, not whether an error recurs. None
// renders nothing (the run's own status already tells the story until the PR merges).
const VERIFY_STATUS_META: Record<number, FixStatusMeta & { bumpKey?: string }> = {
  [VerifyStatus.Watching]: { key: "watching", className: "text-warning", bumpKey: "bumpWatching" },
  [VerifyStatus.Held]: { key: "held", className: "text-success", bumpKey: "bumpHeld" },
  [VerifyStatus.DidNotHold]: {
    key: "didNotHold",
    className: "text-error",
    bumpKey: "bumpDidNotHold",
  },
  [VerifyStatus.CannotVerify]: { key: "cannotVerify", className: "text-muted-foreground" },
};

export function verifyStatusMeta(
  status: number | undefined,
  kind: FixKind = FixKind.IssueFix,
): FixStatusMeta | null {
  const entry = status === undefined ? undefined : VERIFY_STATUS_META[status];
  if (entry === undefined) {
    return null;
  }
  return {
    key: (kind === FixKind.CveBump && entry.bumpKey) || entry.key,
    className: entry.className,
  };
}

// Why a fix run failed. Values match backend Condux.Core.FixEngine.FixFailureReason and the wire value,
// compared with the backend enum by fix-format.test.ts.
export enum FixFailureReason {
  Unknown = 0,
  NoSourceFiles = 1,
  SourceHostAccess = 2,
  ModelKeyRejected = 3,
  ModelUnavailable = 4,
  NoUsableFix = 5,
  TimedOut = 6,
  Interrupted = 7,
  ModelRefused = 8,
  AgentBlocked = 9,
  StoppedExternally = 10,
  SourceFilesUnreadable = 11,
}

// Where the next step for a failure is done, when it is somewhere other than the issue itself.
export enum FailureRemedy {
  RetryFromIssue = "retryFromIssue",
  ProjectGitHub = "projectGitHub",
  AiProvider = "aiProvider",
  // Asking again would fail the same way and Condux has nowhere to send the user, so no next step.
  None = "none",
}

export type FixFailureMeta = { key: string; remedy: FailureRemedy };

// Which kind of run failed. The explanations are shared except where the issue-fix text would send the
// reader somewhere wrong: a CVE bump reads dependency manifests, not the files a stack trace names.
export enum FixKind {
  IssueFix = "issueFix",
  CveBump = "cveBump",
}

type FailureEntry = FixFailureMeta & { bumpKey?: string };

// FixFailureReason -> an explanation key under "fixes.detail.failure" plus where to go next.
const FIX_FAILURE_META: Record<number, FailureEntry> = {
  [FixFailureReason.Unknown]: { key: "unknown", remedy: FailureRemedy.RetryFromIssue },
  [FixFailureReason.NoSourceFiles]: {
    key: "noSourceFiles",
    remedy: FailureRemedy.ProjectGitHub,
    bumpKey: "noManifestFiles",
  },
  [FixFailureReason.SourceHostAccess]: {
    key: "sourceHostAccess",
    remedy: FailureRemedy.ProjectGitHub,
  },
  [FixFailureReason.ModelKeyRejected]: {
    key: "modelKeyRejected",
    remedy: FailureRemedy.AiProvider,
  },
  [FixFailureReason.ModelUnavailable]: {
    key: "modelUnavailable",
    remedy: FailureRemedy.RetryFromIssue,
  },
  [FixFailureReason.NoUsableFix]: { key: "noUsableFix", remedy: FailureRemedy.RetryFromIssue },
  [FixFailureReason.TimedOut]: { key: "timedOut", remedy: FailureRemedy.RetryFromIssue },
  [FixFailureReason.Interrupted]: { key: "interrupted", remedy: FailureRemedy.RetryFromIssue },
  [FixFailureReason.ModelRefused]: { key: "modelRefused", remedy: FailureRemedy.RetryFromIssue },
  [FixFailureReason.AgentBlocked]: { key: "agentBlocked", remedy: FailureRemedy.RetryFromIssue },
  [FixFailureReason.SourceFilesUnreadable]: {
    key: "sourceFilesUnreadable",
    remedy: FailureRemedy.None,
    bumpKey: "manifestFilesUnreadable",
  },
  [FixFailureReason.StoppedExternally]: {
    key: "stoppedExternally",
    remedy: FailureRemedy.RetryFromIssue,
  },
};

// Null when the run did not fail, or failed before reasons were recorded, so nothing is claimed. A
// value this build does not know reads as Unknown rather than as a guess.
export function fixFailureMeta(
  reason: number | null | undefined,
  kind: FixKind = FixKind.IssueFix,
): FixFailureMeta | null {
  if (reason === null || reason === undefined) {
    return null;
  }
  const entry = FIX_FAILURE_META[reason] ?? FIX_FAILURE_META[FixFailureReason.Unknown];
  const key = kind === FixKind.CveBump && entry.bumpKey ? entry.bumpKey : entry.key;
  return { key, remedy: entry.remedy };
}

// A request for another run is still waiting while the newest run is the one it replaced (null when
// there was none). The request writes its run before it answers, but the list on screen was read before
// that, so until it is read again it shows the old run, and offering another request over it would ask
// twice. The server refuses that second request anyway; this only keeps the button from offering it.
// undefined means no request is outstanding.
export function isAwaitingNewRun(
  replacedId: string | null | undefined,
  newestId: string | null | undefined,
): boolean {
  return (newestId ?? null) === replacedId;
}

// The provider a run queued for a customer's runner carries, as written by the control-plane's
// PostgresJobLeaseStore.RunnerProvider.
const RUNNER_PROVIDER = "runner";

// Whether to offer an admin the cancel: a pending run, or a runner's running one, whose lease may have
// lapsed. Only an offer: the server decides from the lease and refuses with fix_not_cancellable while a runner
// still holds it, and a hosted run that is running is never offered, since the Conductor is executing it.
export function canOfferCancel(status: number, provider: string): boolean {
  return (
    status === FixStatus.Pending || (status === FixStatus.Running && provider === RUNNER_PROVIDER)
  );
}

// A run that ended without a PR, so the issue may ask for another.
export function canRequestAnotherFix(status: number): boolean {
  return status === FixStatus.Failed || status === FixStatus.Cancelled;
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
  fix_in_progress: "inProgress",
  ai_fix_quota_exceeded: "quotaExceeded",
  ai_fix_cost_cap_exceeded: "costCapExceeded",
};

// Map a failed trigger to a label key under "issues.fix": the expected control-plane rejections (no
// repo linked yet, a run already queued or running, the monthly allowance spent, the compute ceiling
// reached, or too low a role) get a specific message; else generic.
export function fixErrorKey(error: unknown): string {
  const codeKey = lookUpCodeKey(FIX_ERROR_KEYS, readErrorCode(error));
  if (codeKey !== undefined) {
    return codeKey;
  }
  return readErrorStatus(error) === 403 ? "forbidden" : "failed";
}
