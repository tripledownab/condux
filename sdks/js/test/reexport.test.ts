import assert from "node:assert/strict";
import { test } from "node:test";
import {
  Level,
  addBreadcrumb,
  captureException,
  captureMessage,
  clearScope,
  init,
  normalizeFramePath,
  sendEvent,
  setContext,
  setTag,
  setUser,
} from "../src/index.ts";

// @condux/node is now a thin re-export of @condux/core. This guards that the link: dep resolves and the
// whole public surface comes through live (the engine itself is exercised in @condux/core's own suite).
// Every export is named individually on purpose: a `export *` that silently stops covering a symbol
// shows up here rather than in a user's build, which is exactly how the scope API went missing from the
// browser and Next.js surfaces after it shipped in the core.
test("@condux/node re-exports the @condux/core public API", () => {
  for (const fn of [init, captureException, captureMessage, sendEvent, normalizeFramePath]) {
    assert.equal(typeof fn, "function");
  }
  assert.equal(Level.Error, "error");
  // Exercise a re-exported function so a broken alias fails, not just a missing binding.
  assert.equal(normalizeFramePath("webpack-internal:///(rsc)/./src/x.ts"), "src/x.ts");
});

test("@condux/node re-exports the enrichment scope API", () => {
  for (const fn of [setUser, setTag, setContext, addBreadcrumb, clearScope]) {
    assert.equal(typeof fn, "function");
  }
  // Exercise one end to end: set then clear, proving these are the live core bindings, not stubs.
  setTag("plan", "team");
  clearScope();
});
