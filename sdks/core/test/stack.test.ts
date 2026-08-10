import assert from "node:assert/strict";
import { test } from "node:test";
import { type FetchLike, captureException, init, normalizeFramePath } from "../src/index.ts";

const DSN = "http://pub123@relay.test/7";

// A fetch that records the last request body, so a test can inspect the emitted frames.
function recordingFetch() {
  const sent: string[] = [];
  const fetch: FetchLike = async (_url, init) => {
    sent.push(init.body);
    return { status: 202, headers: { get: () => null } };
  };
  return { fetch, last: () => JSON.parse(sent[sent.length - 1]) };
}

test("normalizeFramePath strips bundler loader/scheme prefixes to a source-relative path", () => {
  const cases: [string, string][] = [
    // Next.js webpack dev frames: strip the webpack-internal scheme + the (rsc)/(ssr) layer marker + "./".
    ["webpack-internal:///(rsc)/./src/app/api/boom/route.ts", "src/app/api/boom/route.ts"],
    ["webpack-internal:///(ssr)/./app/checkout.ts", "app/checkout.ts"],
    // webpack:// with a namespace, and Turbopack's [project] root.
    ["webpack://_N_E/./src/lib/db.ts", "src/lib/db.ts"],
    ["[project]/src/worker.ts", "src/worker.ts"],
    // Node's ESM file URL keeps the absolute path (just drops the scheme).
    ["file:///app/src/index.js", "/app/src/index.js"],
    // A cache-buster query is dropped.
    ["/app/dist/server.js?abc123", "/app/dist/server.js"],
    // A bundled framework frame normalizes to its node_modules path (stays out-of-app).
    [
      "webpack-internal:///(rsc)/./node_modules/next/dist/server/route.js",
      "node_modules/next/dist/server/route.js",
    ],
  ];
  for (const [raw, expected] of cases) {
    assert.equal(normalizeFramePath(raw), expected, raw);
  }
});

test("normalizeFramePath passes an already-clean or unrecognized path through unchanged", () => {
  // Additive: no recognized prefix means identity, so it can never break a plain path.
  assert.equal(normalizeFramePath("/app/src/plain.js"), "/app/src/plain.js");
  assert.equal(normalizeFramePath("node:internal/process/task_queues"), "node:internal/process/task_queues");
  assert.equal(normalizeFramePath("src/already/relative.ts"), "src/already/relative.ts");
});

test("captureException normalizes bundled Next server frames and classifies in-app correctly", async () => {
  const { fetch, last } = recordingFetch();
  init({ dsn: DSN, fetch });

  const error = new TypeError("boom");
  // A synthetic V8 stack as a bundled Next server emits it (newest-first).
  error.stack = [
    "TypeError: boom",
    "    at handler (webpack-internal:///(rsc)/./src/app/api/boom/route.ts:12:5)",
    "    at run (webpack-internal:///(rsc)/./node_modules/next/dist/server/route.js:99:1)",
  ].join("\n");

  await captureException(error);

  // Frames are oldest-first: the framework frame first (out-of-app), the app route last (in-app).
  const frames = last().exception.values[0].stacktrace.frames;
  assert.equal(frames.length, 2);
  assert.equal(frames[0].filename, "node_modules/next/dist/server/route.js");
  assert.equal(frames[0].in_app, false);
  assert.equal(frames[1].filename, "src/app/api/boom/route.ts");
  assert.equal(frames[1].in_app, true);
  assert.equal(frames[1].lineno, 12);
});
