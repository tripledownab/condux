import { describe, expect, it } from "vitest";
import { safeInternalPath } from "./safe-path";

describe("safeInternalPath", () => {
  it("accepts an app-internal path", () => {
    expect(safeInternalPath("/invite?token=abc")).toBe("/invite?token=abc");
    expect(safeInternalPath("/settings")).toBe("/settings");
  });

  it("rejects protocol-relative and cross-origin targets", () => {
    expect(safeInternalPath("//evil.com")).toBeNull();
    expect(safeInternalPath("/\\evil.com")).toBeNull();
    expect(safeInternalPath("https://evil.com")).toBeNull();
  });

  it("rejects empty or non-path input", () => {
    expect(safeInternalPath(null)).toBeNull();
    expect(safeInternalPath(undefined)).toBeNull();
    expect(safeInternalPath("")).toBeNull();
    expect(safeInternalPath("relative")).toBeNull();
  });
});
