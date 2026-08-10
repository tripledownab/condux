import type { FixListItem } from "@/src/api/generated/model";
import { FixStatus, VerifyStatus } from "@/src/issues/fix-format";

// The lifecycle buckets a fix falls into, in display order. Mutually exclusive: fixBucketOf assigns
// exactly one. Archived is not a bucket here — archived fixes are fetched separately (?archived=true).
export enum FixBucket {
  InProgress = "in-progress",
  Ready = "ready",
  Verifying = "verifying",
  Held = "held",
  DidNotHold = "did-not-hold",
  Failed = "failed",
}

// Display order for the views rail.
export const FIX_BUCKETS: FixBucket[] = [
  FixBucket.InProgress,
  FixBucket.Ready,
  FixBucket.Verifying,
  FixBucket.Held,
  FixBucket.DidNotHold,
  FixBucket.Failed,
];

// Which bucket a fix belongs to. Verification state leads (a merged fix is verifying or has
// concluded), then a failed/cancelled run, then a draft PR ready for review, else it is still running.
export function fixBucketOf(item: Pick<FixListItem, "status" | "verifyStatus">): FixBucket {
  if (item.verifyStatus === VerifyStatus.Held) {
    return FixBucket.Held;
  }
  if (item.verifyStatus === VerifyStatus.DidNotHold) {
    return FixBucket.DidNotHold;
  }
  if (item.verifyStatus === VerifyStatus.Watching) {
    return FixBucket.Verifying;
  }
  if (item.status === FixStatus.Failed || item.status === FixStatus.Cancelled) {
    return FixBucket.Failed;
  }
  if (item.status === FixStatus.Succeeded) {
    return FixBucket.Ready;
  }
  return FixBucket.InProgress;
}

export function countByBucket(items: FixListItem[]): Record<FixBucket, number> {
  const counts: Record<FixBucket, number> = {
    [FixBucket.InProgress]: 0,
    [FixBucket.Ready]: 0,
    [FixBucket.Verifying]: 0,
    [FixBucket.Held]: 0,
    [FixBucket.DidNotHold]: 0,
    [FixBucket.Failed]: 0,
  };
  for (const item of items) {
    counts[fixBucketOf(item)]++;
  }
  return counts;
}
