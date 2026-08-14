import assert from "node:assert/strict";
import { test } from "node:test";
import { captureException, captureMessage } from "../src/index.ts";

// Own file, deliberately: it must run in a process where init was NEVER called, which file-level
// isolation guarantees and a shared file cannot.

test("capture before init never throws — it warns once and reports a dropped event", async () => {
  const warnings: string[] = [];
  const original = console.warn;
  console.warn = (message: string) => warnings.push(message);
  try {
    // Reporting never throws: throwing here would turn a HANDLED error into an unhandled one in the
    // exact code path where the caller is already dealing with a failure.
    const first = await captureException(new Error("handled but unreported"));
    const second = await captureMessage("also dropped");

    assert.equal(first.ok, false);
    assert.equal(first.error, "not_initialized");
    assert.equal(second.ok, false);
    assert.equal(warnings.length, 1); // once, not per event
    assert.match(warnings[0], /before init/);
  } finally {
    console.warn = original;
  }
});
