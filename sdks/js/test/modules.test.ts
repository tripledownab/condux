import assert from "node:assert/strict";
import { mkdtempSync, mkdirSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { captureException, clearModules, init } from "../src/index.ts";
import { collectModules } from "../src/modules.ts";

/** A fetch that records what went on the wire, so the assertion is about the payload, not a spy call. */
function recordingFetch() {
  const sent: string[] = [];
  return {
    fetch: async (_url: string, request: { body: string }) => {
      sent.push(request.body);
      return { status: 202, headers: { get: () => null } };
    },
    last: () => JSON.parse(sent[sent.length - 1]),
  };
}

/**
 * Builds a real node_modules tree on disk rather than mocking the filesystem. The layouts this has to
 * cope with (scoped directories, pnpm's .pnpm store, a package with no manifest) are filesystem
 * shapes, so a mock would only assert that the code matches the shape I imagined it would meet.
 */
function tree(spec: Record<string, string | null>): string {
  const root = mkdtempSync(join(tmpdir(), "condux-modules-"));
  for (const [path, version] of Object.entries(spec)) {
    const directory = join(root, path);
    mkdirSync(directory, { recursive: true });
    if (version !== null) {
      const name = path.slice(path.lastIndexOf("node_modules/") + "node_modules/".length);
      writeFileSync(join(directory, "package.json"), JSON.stringify({ name, version }));
    }
  }
  return root;
}

test("reads the installed packages out of a flat node_modules", () => {
  const root = tree({
    "node_modules/lodash": "4.17.11",
    "node_modules/express": "4.16.0",
  });

  assert.deepEqual(collectModules({ cwd: root }), { lodash: "4.17.11", express: "4.16.0" });
});

test("reads scoped packages, which sit one level deeper", () => {
  const root = tree({ "node_modules/@acme/widgets": "2.1.0" });

  assert.deepEqual(collectModules({ cwd: root }), { "@acme/widgets": "2.1.0" });
});

/**
 * pnpm's default layout symlinks only DIRECT dependencies into the top level and keeps everything
 * else under .pnpm. Without descending it, a pnpm application would report its handful of direct
 * dependencies and silently omit every transitive one, which is where advisories usually land: the
 * feature would answer "not observed" for exactly the cases it exists to answer.
 */
test("descends pnpm's store so transitive packages are not silently missing", () => {
  const root = tree({
    "node_modules/express": "4.16.0",
    "node_modules/.pnpm/qs@6.5.1/node_modules/qs": "6.5.1",
    "node_modules/.pnpm/ms@2.0.0/node_modules/ms": "2.0.0",
  });

  assert.deepEqual(collectModules({ cwd: root }), {
    express: "4.16.0",
    qs: "6.5.1",
    ms: "2.0.0",
  });
});

/**
 * The layout that shipped broken, reproduced. In a pnpm WORKSPACE each package gets a node_modules
 * holding only its direct dependencies, while the store with everything else sits at the workspace
 * root. Collecting from only the nearest one reported 38 packages in production when the store one
 * level up held 922, so every transitive dependency was missing, which is where advisories live.
 *
 * Revert to stopping at the first node_modules and this test fails.
 */
test("collects from a workspace root's store, not just the package's own node_modules", () => {
  const root = tree({
    // The workspace root: a small top level plus the real store.
    "node_modules/shared-dep": "1.0.0",
    "node_modules/.pnpm/qs@6.5.1/node_modules/qs": "6.5.1",
    "node_modules/.pnpm/ms@2.0.0/node_modules/ms": "2.0.0",
    // The package being run, with only its direct dependency linked in.
    "web/node_modules/next": "16.2.11",
  });

  assert.deepEqual(collectModules({ cwd: join(root, "web") }), {
    next: "16.2.11",
    "shared-dep": "1.0.0",
    qs: "6.5.1",
    ms: "2.0.0",
  });
});

/** The nearest copy wins, because that is the one Node resolves. */
test("a package's own copy beats the workspace root's copy of the same name", () => {
  const root = tree({
    "node_modules/lodash": "3.0.0",
    "web/node_modules/lodash": "4.17.21",
  });

  assert.deepEqual(collectModules({ cwd: join(root, "web") }), { lodash: "4.17.21" });
});

test("ignores dot directories other than the pnpm store", () => {
  const root = tree({
    "node_modules/lodash": "4.17.11",
    "node_modules/.cache/something": "9.9.9",
    "node_modules/.bin": null,
  });

  assert.deepEqual(collectModules({ cwd: root }), { lodash: "4.17.11" });
});

test("finds node_modules above the working directory", () => {
  const root = tree({ "node_modules/lodash": "4.17.11", "src/deep/nested": null });

  assert.deepEqual(collectModules({ cwd: join(root, "src/deep/nested") }), { lodash: "4.17.11" });
});

test("reports nothing when there is no node_modules at all", () => {
  const root = tree({ src: null });

  assert.deepEqual(collectModules({ cwd: root }), {});
});

/**
 * This runs during init in an application that never asked for a filesystem walk, so one unreadable
 * or malformed manifest must cost that entry and nothing else. A throw here would take out the host
 * application's startup.
 */
test("a directory with a broken or absent manifest costs only itself", () => {
  const root = tree({ "node_modules/lodash": "4.17.11", "node_modules/broken": null });
  writeFileSync(join(root, "node_modules/broken/package.json"), "{ not json");

  assert.deepEqual(collectModules({ cwd: root }), { lodash: "4.17.11" });
});

test("a manifest without a usable name or version is skipped", () => {
  const root = tree({ "node_modules/lodash": "4.17.11", "node_modules/weird": null });
  writeFileSync(join(root, "node_modules/weird/package.json"), JSON.stringify({ name: "weird" }));

  assert.deepEqual(collectModules({ cwd: root }), { lodash: "4.17.11" });
});

/**
 * The shallowest copy wins, because that is the one Node would resolve. Reporting the nested version
 * would name a version the running code is not using.
 */
test("a nested duplicate does not override the resolved top-level copy", () => {
  const root = tree({
    "node_modules/lodash": "4.17.21",
    "node_modules/.pnpm/lodash@3.0.0/node_modules/lodash": "3.0.0",
  });

  assert.deepEqual(collectModules({ cwd: root }), { lodash: "4.17.21" });
});

test("the scan ceiling bounds the work on a pathological tree", () => {
  const spec: Record<string, string> = {};
  for (let i = 0; i < 40; i++) {
    spec[`node_modules/pkg-${i}`] = "1.0.0";
  }
  const root = tree(spec);

  assert.equal(Object.keys(collectModules({ cwd: root, maxScanned: 10 })).length, 10);
});

/**
 * The two halves joined. The collector and the core's carrier are each tested alone above; this is
 * the only thing asserting that @condux/node's init actually connects them, which is what a Node
 * developer gets by installing the package and changing nothing else.
 */
test("init collects the installed tree so the next event carries it", async () => {
  clearModules();
  const { fetch, last } = recordingFetch();

  init({ dsn: "http://pub123@relay.test/7", fetch });
  await captureException(new Error("boom"));

  const modules = last().modules;
  assert.ok(modules !== undefined, "an event from @condux/node should carry the inventory");
  // This package's own dependency, so the assertion names something that must be there rather than
  // just counting keys.
  assert.equal(typeof modules["@condux/core"], "string");
});

test("sendModules false leaves the inventory off entirely", async () => {
  clearModules();
  const { fetch, last } = recordingFetch();

  init({ dsn: "http://pub123@relay.test/7", fetch, sendModules: false });
  await captureException(new Error("boom"));

  assert.equal("modules" in last(), false);
});
