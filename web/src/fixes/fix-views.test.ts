import { describe, expect, it } from "vitest";
import type { FixListItem } from "@/src/api/generated/model";
import { FixStatus, VerifyStatus } from "@/src/issues/fix-format";
import { countByBucket, FixBucket, fixBucketOf } from "./fix-views";

describe("fixBucketOf", () => {
  const cases: [FixStatus, VerifyStatus, FixBucket][] = [
    [FixStatus.Pending, VerifyStatus.None, FixBucket.InProgress],
    [FixStatus.Running, VerifyStatus.None, FixBucket.InProgress],
    [FixStatus.Succeeded, VerifyStatus.None, FixBucket.Ready], // draft PR ready
    [FixStatus.Succeeded, VerifyStatus.Watching, FixBucket.Verifying], // merged, verifying
    [FixStatus.Succeeded, VerifyStatus.Held, FixBucket.Held],
    [FixStatus.Succeeded, VerifyStatus.DidNotHold, FixBucket.DidNotHold],
    [FixStatus.Failed, VerifyStatus.None, FixBucket.Failed],
    [FixStatus.Cancelled, VerifyStatus.None, FixBucket.Failed],
  ];

  it.each(cases)(
    "status %i verify %i lands in the right bucket",
    (status, verifyStatus, bucket) => {
      expect(fixBucketOf({ status, verifyStatus })).toBe(bucket);
    },
  );

  it("counts each bucket over a mixed list", () => {
    const counts = countByBucket([
      { status: FixStatus.Running, verifyStatus: VerifyStatus.None },
      { status: FixStatus.Succeeded, verifyStatus: VerifyStatus.None },
      { status: FixStatus.Succeeded, verifyStatus: VerifyStatus.Held },
      { status: FixStatus.Failed, verifyStatus: VerifyStatus.None },
    ] as FixListItem[]);
    expect(counts[FixBucket.InProgress]).toBe(1);
    expect(counts[FixBucket.Ready]).toBe(1);
    expect(counts[FixBucket.Held]).toBe(1);
    expect(counts[FixBucket.Failed]).toBe(1);
  });
});
