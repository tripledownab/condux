import assert from "node:assert/strict";
import { afterEach, test } from "node:test";
import { type FetchLike, Level, captureMessage, register } from "../src/index.ts";
// Reached through the core rather than the adapter's own surface: a Next app never calls these, so
// re-exporting them from @condux/nextjs would be public API nobody asked for.
import { clearModules } from "@condux/core";

const DSN = "http://pub123@relay.test/7";

function recordingFetch() {
  const sent: string[] = [];
  const fetch: FetchLike = async (_url, init) => {
    sent.push(init.body);
    return { status: 202, headers: { get: () => null } };
  };
  return { fetch, last: () => JSON.parse(sent[sent.length - 1]) };
}

const runtimeBefore = process.env.NEXT_RUNTIME;

afterEach(() => {
  clearModules();
  if (runtimeBefore === undefined) {
    delete process.env.NEXT_RUNTIME;
  } else {
    process.env.NEXT_RUNTIME = runtimeBefore;
  }
});

/**
 * register runs on BOTH the nodejs and edge runtimes from one function, and reading the installed tree
 * needs a filesystem. The guard is what keeps node:fs out of the edge bundle, verified against a real
 * Turbopack build; this pins the runtime half of it, which a bundler check cannot see.
 */
test("the inventory is collected on the Node runtime", async () => {
  clearModules();
  process.env.NEXT_RUNTIME = "nodejs";
  const { fetch, last } = recordingFetch();

  register({ dsn: DSN, fetch });
  // Collection is dynamic and not awaited, so instrumentation stays synchronous; let it land.
  await new Promise((resolve) => setTimeout(resolve, 50));
  await captureMessage("hi", Level.Info);

  const modules = last().modules;
  assert.ok(modules !== undefined, "a Node-runtime event should carry the inventory");
  assert.equal(typeof modules["@condux/core"], "string");
});

test("the inventory is not collected on the edge runtime", async () => {
  clearModules();
  process.env.NEXT_RUNTIME = "edge";
  const { fetch, last } = recordingFetch();

  register({ dsn: DSN, fetch });
  await new Promise((resolve) => setTimeout(resolve, 50));
  await captureMessage("hi", Level.Info);

  assert.equal("modules" in last(), false);
});
