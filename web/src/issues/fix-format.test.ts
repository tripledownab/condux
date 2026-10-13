import { describe, expect, it } from "vitest";
import messages from "@/messages/en.json";
import { ConduxApiError } from "@/src/api/fetcher";
import { readBackendEnum, toNumericMembers } from "@/src/test-utils/backend-enums";
import {
  FixFailureReason,
  FixKind,
  fixErrorKey,
  fixFailureMeta,
  isAwaitingNewRun,
  VerifyStatus,
  verifyStatusMeta,
} from "./fix-format";

// Every reason the backend can store must have its own explanation, or the notice falls back to the
// Unknown text and tells the user less than the run recorded.
describe("fixFailureMeta", () => {
  const explanations: Record<string, string> = messages.fixes.detail.failure;
  const reasons = Object.values(FixFailureReason).filter((value) => typeof value === "number");

  const cases = reasons.flatMap((reason) =>
    [FixKind.IssueFix, FixKind.CveBump].map((kind) => [reason, kind]),
  );

  it.each(cases)("explains reason %i for a %s with a message of its own", (reason, kind) => {
    const meta = fixFailureMeta(reason as number, kind as FixKind);
    expect(meta).not.toBeNull();
    expect(explanations[meta?.key ?? ""]).toBeTruthy();
    if (reason !== FixFailureReason.Unknown) {
      expect(meta?.key).not.toBe("unknown");
    }
  });

  it("tells a bump about its manifests where an issue fix hears about stack traces", () => {
    expect(fixFailureMeta(FixFailureReason.NoSourceFiles)?.key).toBe("noSourceFiles");
    expect(fixFailureMeta(FixFailureReason.NoSourceFiles, FixKind.CveBump)?.key).toBe(
      "noManifestFiles",
    );
    expect(fixFailureMeta(FixFailureReason.TimedOut, FixKind.CveBump)?.key).toBe("timedOut");
  });

  // The API sends the reason as a bare integer, so the copy here and the backend enum are held together
  // only by this: a reason added there alone would reach users as "could not classify".
  it("lists exactly the reasons the backend enum declares, with the same numbers", () => {
    expect(toNumericMembers(FixFailureReason)).toEqual(
      readBackendEnum("FixEngine/FixFailures.cs", "FixFailureReason", 6),
    );
  });

  // Every verification state but None must have its own label, or a run in that state renders blank.
  it("labels every verification state but None, for either kind of fix", () => {
    for (const kind of [FixKind.IssueFix, FixKind.CveBump]) {
      for (const status of Object.values(toNumericMembers(VerifyStatus))) {
        const meta = verifyStatusMeta(status, kind);
        if (status === VerifyStatus.None) {
          expect(meta).toBeNull();
        } else {
          expect(
            meta?.key &&
              messages.issues.fix.verify[meta.key as keyof typeof messages.issues.fix.verify],
          ).toBeTruthy();
        }
      }
    }
  });

  // The same holds for verification states: a state the backend adds alone would never be labelled.
  it("lists exactly the verification states the backend enum declares, with the same numbers", () => {
    expect(toNumericMembers(VerifyStatus)).toEqual(
      readBackendEnum("FixEngine/FixVerification.cs", "VerifyStatus", 5),
    );
  });

  it("reads a reason this build does not know as Unknown, and no reason as nothing", () => {
    expect(fixFailureMeta(999)?.key).toBe("unknown");
    expect(fixFailureMeta(null)).toBeNull();
  });
});

describe("fixErrorKey", () => {
  const refused = (status: number, code?: string) =>
    new ConduxApiError("POST", "/api/projects/7/issues/x/fix", status, code);

  it("names an expected refusal, then a forbidden role, then falls back", () => {
    expect(fixErrorKey(refused(409, "ai_fix_quota_exceeded"))).toBe("quotaExceeded");
    expect(fixErrorKey(refused(409, "fix_in_progress"))).toBe("inProgress");
    expect(fixErrorKey(refused(403))).toBe("forbidden");
    // A known code wins over the status it arrived with.
    expect(fixErrorKey(refused(403, "no_repo_linked"))).toBe("noRepo");
    expect(fixErrorKey(refused(500))).toBe("failed");
    expect(fixErrorKey(new Error("network down"))).toBe("failed");
  });
});

describe("isAwaitingNewRun", () => {
  it("waits while the newest run is still the one the request replaced", () => {
    expect(isAwaitingNewRun("run-1", "run-1")).toBe(true);
    expect(isAwaitingNewRun("run-1", "run-2")).toBe(false);
  });

  it("waits after a first request until any run appears", () => {
    expect(isAwaitingNewRun(null, undefined)).toBe(true);
    expect(isAwaitingNewRun(null, "run-1")).toBe(false);
  });

  it("never waits when no request is outstanding", () => {
    expect(isAwaitingNewRun(undefined, undefined)).toBe(false);
    expect(isAwaitingNewRun(undefined, "run-1")).toBe(false);
  });
});
