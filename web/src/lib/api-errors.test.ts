import { describe, expect, it } from "vitest";
import { ConduxApiError } from "@/src/api/fetcher";
import { lookUpCodeKey, readErrorCode, readErrorStatus } from "./api-errors";

describe("readErrorCode", () => {
  it("reads the code of a control-plane failure and nothing else", () => {
    expect(readErrorCode(new ConduxApiError("POST", "/api/x", 409, "email_taken"))).toBe(
      "email_taken",
    );
    expect(readErrorCode(new ConduxApiError("POST", "/api/x", 500))).toBeUndefined();
    expect(readErrorCode(new Error("network down"))).toBeUndefined();
    // Only a real control-plane failure: an object that merely looks like one is not trusted.
    expect(readErrorCode({ code: "email_taken", status: 409 })).toBeUndefined();
  });
});

describe("readErrorStatus", () => {
  it("reads the status of a control-plane failure and nothing else", () => {
    expect(readErrorStatus(new ConduxApiError("GET", "/api/x", 403))).toBe(403);
    expect(readErrorStatus(new Error("network down"))).toBeUndefined();
    expect(readErrorStatus({ code: "x", status: 403 })).toBeUndefined();
  });
});

describe("lookUpCodeKey", () => {
  const keys = { email_taken: "errors.emailTaken" };

  it("maps a known code to its key", () => {
    expect(lookUpCodeKey(keys, "email_taken")).toBe("errors.emailTaken");
  });

  it("knows nothing of an unknown, missing or inherited code", () => {
    expect(lookUpCodeKey(keys, "nope")).toBeUndefined();
    expect(lookUpCodeKey(keys, undefined)).toBeUndefined();
    expect(lookUpCodeKey(keys, null)).toBeUndefined();
    // A code from a URL or a server is text anyone can choose; these name functions on every object.
    expect(lookUpCodeKey(keys, "constructor")).toBeUndefined();
    expect(lookUpCodeKey(keys, "toString")).toBeUndefined();
  });
});
