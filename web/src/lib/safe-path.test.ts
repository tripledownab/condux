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

  // The URL parser REMOVES tab, line feed and carriage return from anywhere in the input before
  // resolving it, so each of these reaches the browser as a protocol-relative "//evil.com" while the
  // raw string passes a prefix test. Testing the raw string is testing something the browser never
  // sees. Every case here is a redirect to another origin if the control character is not rejected.
  it("rejects a target whose control characters the URL parser strips", () => {
    expect(safeInternalPath("/\t//evil.com")).toBeNull();
    expect(safeInternalPath("/\n//evil.com")).toBeNull();
    expect(safeInternalPath("/\r//evil.com")).toBeNull();
    // The backslash variant, reached the same way: "/<tab>\evil.com" strips to "/\evil.com", which
    // browsers normalize to "//evil.com".
    expect(safeInternalPath("/\t\\evil.com")).toBeNull();
    // Split across the separator, so neither half looks like a prefix on its own.
    expect(safeInternalPath("/\t/\t/evil.com")).toBeNull();
  });

  it("rejects empty or non-path input", () => {
    expect(safeInternalPath(null)).toBeNull();
    expect(safeInternalPath(undefined)).toBeNull();
    expect(safeInternalPath("")).toBeNull();
    expect(safeInternalPath("relative")).toBeNull();
  });
});
