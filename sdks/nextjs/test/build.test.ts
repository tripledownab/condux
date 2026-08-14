import assert from "node:assert/strict";
import { existsSync } from "node:fs";
import { mkdir, mkdtemp, readFile, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { debugIdFromContent, processClientBuild, stampChunk } from "../src/build.ts";

// Turbopack-shaped on purpose: the map's name shares nothing with the chunk's, so only following the
// chunk's own sourceMappingURL pointer can pair them (the bug the name convention had).
const CHUNK = 'console.log("hi");\n//# sourceMappingURL=34hirxaxltle0.js.map\n';
const MAP = JSON.stringify({ version: 3, sources: ["src/checkout.ts"], mappings: "AAAA" });

async function buildDir(): Promise<{ distDir: string; jsPath: string; mapPath: string }> {
  const distDir = await mkdtemp(join(tmpdir(), "condux-build-"));
  await mkdir(join(distDir, "static/chunks"), { recursive: true });
  await mkdir(join(distDir, "server"), { recursive: true });
  const jsPath = join(distDir, "static/chunks/00d3wejgy8s0v.js");
  const mapPath = join(distDir, "static/chunks/34hirxaxltle0.js.map");
  await writeFile(jsPath, CHUNK);
  await writeFile(mapPath, MAP);
  await writeFile(join(distDir, "server/page.js"), "server();\n");
  return { distDir, jsPath, mapPath };
}

function recordingFetch(status = 200) {
  const calls: { url: string; body: string }[] = [];
  const doFetch = (async (url: RequestInfo | URL, init?: RequestInit) => {
    calls.push({ url: String(url), body: String(init?.body) });
    return new Response(null, { status });
  }) as typeof fetch;
  return { doFetch, calls };
}

test("stamping appends the registration snippet and the debugId comment", () => {
  const id = debugIdFromContent(CHUNK);
  const stamped = stampChunk(CHUNK, id);

  // The original code is untouched at the top (appended-only, so the map's mappings stay valid).
  assert.ok(stamped.startsWith('console.log("hi");'));
  assert.ok(stamped.includes(`_conduxDebugIds[s]="${id}"`));
  assert.ok(stamped.includes(`//# debugId=${id}`));
  assert.ok(!stamped.includes("sourceMappingURL"));
  // With a map still beside the chunk, the pointer survives, re-appended last where tooling expects it.
  assert.ok(stampChunk(CHUNK, id, "34hirxaxltle0.js.map").endsWith("//# sourceMappingURL=34hirxaxltle0.js.map\n"));
  // Deterministic: the same bytes always get the same id, so a re-deploy re-keys identically.
  assert.equal(id, debugIdFromContent(CHUNK));
  assert.match(id, /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/);
});

test("with an upload target the map is uploaded under the CHUNK's name and removed from the output", async () => {
  const { distDir, jsPath, mapPath } = await buildDir();
  const { doFetch, calls } = recordingFetch();

  const result = await processClientBuild({
    distDir,
    upload: { url: "https://app.test", token: "condux_rel_x", release: "1.2.3" },
    fetch: doFetch,
    log: () => {},
  });

  assert.deepEqual(result, { stamped: 1, uploaded: 1, failed: 0, kept: 0 });
  const stampedJs = await readFile(jsPath, "utf8");
  const id = debugIdFromContent(CHUNK);
  assert.ok(stampedJs.includes(`//# debugId=${id}`));
  // The map is gone, so no dangling pointer stays behind.
  assert.ok(!stampedJs.includes("sourceMappingURL"));
  // Keyed by release + the deployed chunk's filename (what a frame's abs_path ends in — NOT the map's
  // own unrelated name) + the same debugId the chunk carries.
  assert.equal(calls.length, 1);
  const url = new URL(calls[0].url);
  assert.equal(url.pathname, "/api/sourcemaps");
  assert.equal(url.searchParams.get("release"), "1.2.3");
  assert.equal(url.searchParams.get("filename"), "00d3wejgy8s0v.js");
  assert.equal(url.searchParams.get("debugId"), id);
  assert.equal((JSON.parse(calls[0].body) as { debugId: string }).debugId, id);
  assert.equal(existsSync(mapPath), false);
});

test("without an upload target the chunk keeps its map pointer so the CLI can still pair them", async () => {
  const { distDir, jsPath, mapPath } = await buildDir();

  const result = await processClientBuild({ distDir, log: () => {} });

  assert.deepEqual(result, { stamped: 1, uploaded: 0, failed: 0, kept: 1 });
  const stampedJs = await readFile(jsPath, "utf8");
  assert.ok(stampedJs.includes("_conduxDebugIds"));
  assert.ok(stampedJs.endsWith("//# sourceMappingURL=34hirxaxltle0.js.map\n"));
  // Never destroy a map before it is stored somewhere.
  assert.equal(existsSync(mapPath), true);
  const map = JSON.parse(await readFile(mapPath, "utf8")) as { debugId: string };
  assert.equal(map.debugId, debugIdFromContent(CHUNK));
});

test("processing is idempotent: a second pass leaves a stamped chunk alone", async () => {
  const { distDir, jsPath } = await buildDir();
  await processClientBuild({ distDir, log: () => {} });
  const afterFirst = await readFile(jsPath, "utf8");

  const second = await processClientBuild({ distDir, log: () => {} });

  // Re-stamping would derive a different id from the now-changed bytes and register the chunk twice.
  assert.equal(second.stamped, 0);
  assert.equal(await readFile(jsPath, "utf8"), afterFirst);
});

test("a failed upload keeps the map and the pointer, and never throws into the build", async () => {
  const { distDir, jsPath, mapPath } = await buildDir();
  const { doFetch } = recordingFetch(500);

  const result = await processClientBuild({
    distDir,
    upload: { url: "https://app.test", token: "condux_rel_x", release: "1.2.3" },
    fetch: doFetch,
    log: () => {},
  });

  assert.deepEqual(result, { stamped: 1, uploaded: 0, failed: 1, kept: 1 });
  assert.equal(existsSync(mapPath), true);
  assert.ok((await readFile(jsPath, "utf8")).endsWith("//# sourceMappingURL=34hirxaxltle0.js.map\n"));
});

test("with an upload target an orphan map still uploads under its name-derived key and leaves the output", async () => {
  // An orphan (no chunk points at it — a pointer-less chunk's map, or a stale leftover) cannot be
  // stamped, but leaving it deployed serves the original source publicly; the 2 orphans in the first
  // prod image are the bug this pins.
  const { distDir } = await buildDir();
  const orphanPath = join(distDir, "static/chunks/orphan.js.map");
  await writeFile(orphanPath, JSON.stringify({ version: 3, mappings: "AAAA", debugId: "1234" }));
  const { doFetch, calls } = recordingFetch();

  const result = await processClientBuild({
    distDir,
    upload: { url: "https://app.test", token: "condux_rel_x", release: "1.2.3" },
    fetch: doFetch,
    log: () => {},
  });

  assert.deepEqual(result, { stamped: 1, uploaded: 2, failed: 0, kept: 0 });
  const orphanCall = calls.find((call) => call.url.includes("filename=orphan.js"));
  assert.ok(orphanCall);
  assert.ok(orphanCall.url.includes("debugId=1234"));
  assert.equal(existsSync(orphanPath), false);
});

test("chunks without a pointer, orphan maps, and files outside static/ are left alone", async () => {
  const { distDir } = await buildDir();
  const bare = join(distDir, "static/chunks/no-pointer.js");
  await writeFile(bare, "console.log(1);\n");
  await writeFile(join(distDir, "static/chunks/stale.js.map"), "{}");

  const result = await processClientBuild({ distDir, log: () => {} });

  assert.equal(result.stamped, 1);
  assert.equal(await readFile(bare, "utf8"), "console.log(1);\n");
  assert.equal(await readFile(join(distDir, "server/page.js"), "utf8"), "server();\n");
});

test("a missing static directory is a quiet no-op (a server-only build target)", async () => {
  const distDir = await mkdtemp(join(tmpdir(), "condux-empty-"));

  const result = await processClientBuild({ distDir, log: () => {} });

  assert.deepEqual(result, { stamped: 0, uploaded: 0, failed: 0, kept: 0 });
});
