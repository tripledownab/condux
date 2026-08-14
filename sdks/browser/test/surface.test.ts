import assert from "node:assert/strict";
import { test } from "node:test";
import * as sdk from "../src/index.ts";

// Pins the public surface: this package re-exports the core SELECTIVELY, which is exactly how the
// enrichment API silently failed to reach it once. A future edit that drops a symbol fails here.
test("the enrichment and capture API is part of the public surface", () => {
  for (const name of [
    "init",
    "captureException",
    "captureMessage",
    "setUser",
    "setTag",
    "setContext",
    "addBreadcrumb",
    "clearScope",
  ]) {
    assert.equal(typeof (sdk as Record<string, unknown>)[name], "function", `missing export: ${name}`);
  }
});
