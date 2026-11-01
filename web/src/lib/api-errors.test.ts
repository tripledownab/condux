import { readdirSync, readFileSync } from "node:fs";
import { join, relative } from "node:path";
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

// Every read of a failed call goes through readErrorCode/readErrorStatus. A cast, a hook typed with
// ConduxApiError as its error, or a parameter typed with it claims a type the error may not have (a network
// failure is a TypeError), so outside the two files that own it no source file may name the type at all,
// whichever spelling it takes. A cast to a shape with a status or a code is refused for the same reason.
describe("reading an API error", () => {
  const root = join(__dirname, "..", "..");
  const owners = new Set(["src/api/fetcher.ts", "src/lib/api-errors.ts"]);

  function sourceFiles(dir: string): string[] {
    return readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
      const path = join(dir, entry.name);
      if (entry.isDirectory()) {
        return entry.name === "generated" || entry.name === "node_modules" ? [] : sourceFiles(path);
      }
      return /\.tsx?$/.test(entry.name) && !/\.test\.tsx?$/.test(entry.name) ? [path] : [];
    });
  }

  it("reads a failed call only through the helpers", () => {
    const files = [...sourceFiles(join(root, "src")), ...sourceFiles(join(root, "app"))];
    const offenders = files
      .map((path) => relative(root, path))
      .filter((path) => !owners.has(path))
      .filter((path) =>
        /\bConduxApiError\b|as \{[^}]*\b(status|code)\b/.test(
          readFileSync(join(root, path), "utf8"),
        ),
      );

    // A scan that read nothing would pass too, so it has to have read the dashboard.
    expect(files.length).toBeGreaterThan(200);
    expect(offenders).toEqual([]);
  });
});
