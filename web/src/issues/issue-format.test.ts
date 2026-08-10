import { describe, expect, it } from "vitest";
import { levelMeta, statusKey } from "./issue-format";

describe("levelMeta", () => {
  it("maps each known level to its label key and brand color", () => {
    expect(levelMeta(5)).toEqual({ key: "fatal", className: "text-fatal" });
    expect(levelMeta(4)).toEqual({ key: "error", className: "text-error" });
    expect(levelMeta(2)).toEqual({ key: "info", className: "text-info" });
  });

  it("falls back to an 'unknown' key for an out-of-range level", () => {
    expect(levelMeta(99)).toEqual({ key: "unknown", className: "text-debug" });
  });
});

describe("statusKey", () => {
  it("maps the numeric status to its label key", () => {
    expect(statusKey(1)).toBe("unresolved");
    expect(statusKey(2)).toBe("resolved");
    expect(statusKey(3)).toBe("ignored");
  });

  it("surfaces an unexpected status as 'unknown' rather than a real status", () => {
    expect(statusKey(99)).toBe("unknown");
  });
});
