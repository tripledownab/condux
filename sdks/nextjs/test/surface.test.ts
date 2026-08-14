import assert from "node:assert/strict";
import { test } from "node:test";
import * as sdk from "../src/index.ts";

// The main entry re-exports the core SELECTIVELY (it must stay server/edge-safe, so it cannot
// export *), which is exactly how the enrichment API once failed to reach this package. Pin it.
test("the enrichment and capture API is part of the public surface", () => {
  for (const name of [
    "register",
    "captureRequestError",
    "initClient",
    "conduxTunnelRoute",
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

test("initClient with no DSN warns instead of silently staying off", () => {
  const previous = process.env.NEXT_PUBLIC_CONDUX_DSN;
  delete process.env.NEXT_PUBLIC_CONDUX_DSN;
  const warnings: string[] = [];
  const original = console.warn;
  console.warn = (message: string) => warnings.push(message);
  try {
    sdk.initClient(); // the misconfiguration the README warns about — must be loud, never silent
    assert.equal(warnings.length, 1);
    assert.match(warnings[0], /NEXT_PUBLIC_CONDUX_DSN/);
  } finally {
    console.warn = original;
    if (previous !== undefined) {
      process.env.NEXT_PUBLIC_CONDUX_DSN = previous;
    }
  }
});
