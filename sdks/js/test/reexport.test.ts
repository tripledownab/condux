import assert from "node:assert/strict";
import { test } from "node:test";
import {
  Level,
  captureException,
  captureMessage,
  init,
  normalizeFramePath,
  sendEvent,
} from "../src/index.ts";

// @condux/node is now a thin re-export of @condux/core. This guards that the link: dep resolves and the
// whole public surface comes through live (the engine itself is exercised in @condux/core's own suite).
test("@condux/node re-exports the @condux/core public API", () => {
  for (const fn of [init, captureException, captureMessage, sendEvent, normalizeFramePath]) {
    assert.equal(typeof fn, "function");
  }
  assert.equal(Level.Error, "error");
  // Exercise a re-exported function so a broken alias fails, not just a missing binding.
  assert.equal(normalizeFramePath("webpack-internal:///(rsc)/./src/x.ts"), "src/x.ts");
});
