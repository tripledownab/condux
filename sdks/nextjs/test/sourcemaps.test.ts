import assert from "node:assert/strict";
import { mkdir, mkdtemp, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { findSourceMaps, uploadSourceMaps } from "../src/sourcemaps.ts";

// A build-output fixture: two .js.map files nested in a chunks dir, plus non-maps that must be ignored.
async function buildFixture() {
  const dir = await mkdtemp(join(tmpdir(), "condux-maps-"));
  await mkdir(join(dir, "static", "chunks"), { recursive: true });
  await writeFile(join(dir, "static", "chunks", "app.js.map"), '{"version":3,"mappings":"AAAA"}');
  await writeFile(join(dir, "static", "chunks", "main.js.map"), '{"version":3,"mappings":"AAAA"}');
  await writeFile(join(dir, "static", "chunks", "app.js"), "console.log(1)"); // the bundle, not a map
  await writeFile(join(dir, "styles.css.map"), "{}"); // a css map, not a js map
  return dir;
}

function recordingFetch(status: number) {
  const calls: Array<{ url: string; init: RequestInit }> = [];
  const fetch = (async (url: string, init: RequestInit) => {
    calls.push({ url: String(url), init });
    return { status };
  }) as unknown as typeof globalThis.fetch;
  return { fetch, calls };
}

test("findSourceMaps returns every .js.map recursively, ignoring other files", async () => {
  const dir = await buildFixture();
  const found = (await findSourceMaps(dir)).map((p) => p.slice(dir.length).replaceAll("\\", "/")).sort();
  assert.deepEqual(found, ["/static/chunks/app.js.map", "/static/chunks/main.js.map"]);
});

test("uploadSourceMaps posts each map with the release token, keyed by release + filename", async () => {
  const dir = await buildFixture();
  const { fetch, calls } = recordingFetch(201);

  const result = await uploadSourceMaps({
    dir,
    url: "https://app.condux.ai/", // trailing slash is trimmed
    release: "v1.2.3",
    token: "condux_rel_x",
    fetch,
  });

  assert.equal(result.uploaded, 2);
  assert.equal(result.failed, 0);
  const urls = calls.map((c) => c.url).sort();
  assert.ok(urls.every((u) => u.startsWith("https://app.condux.ai/api/sourcemaps?")));
  assert.ok(urls.some((u) => u.includes("filename=app.js") && u.includes("release=v1.2.3")));
  assert.ok(urls.some((u) => u.includes("filename=main.js")));
  const headers = calls[0].init.headers as Record<string, string>;
  assert.equal(headers.authorization, "Bearer condux_rel_x");
  assert.equal(calls[0].init.method, "POST");
});

test("uploadSourceMaps reports a failed upload without throwing", async () => {
  const dir = await buildFixture();
  const { fetch } = recordingFetch(401);
  const result = await uploadSourceMaps({ dir, url: "https://x", release: "v1", token: "bad", fetch });
  assert.equal(result.uploaded, 0);
  assert.equal(result.failed, 2);
  assert.equal(result.files[0].ok, false);
  assert.equal(result.files[0].status, 401);
});
