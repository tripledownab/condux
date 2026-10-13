import assert from "node:assert/strict";
import { mkdir, mkdtemp, readFile, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { findSourceMaps } from "../src/map-files.ts";
import { uploadSourceMaps } from "../src/sourcemaps.ts";

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

test("a pointer-paired map uploads under the CHUNK's name, with the stamped debugId", async () => {
  // Turbopack shape: the map's own name shares nothing with the chunk's, so only the chunk's
  // sourceMappingURL pointer can key the upload by the name a stack frame will actually carry.
  const dir = await mkdtemp(join(tmpdir(), "condux-turbo-"));
  await writeFile(join(dir, "00d3wejgy8s0v.js"), "code();\n//# sourceMappingURL=34hirxaxltle0.js.map\n");
  await writeFile(
    join(dir, "34hirxaxltle0.js.map"),
    '{"version":3,"mappings":"AAAA","debugId":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"}',
  );
  const { fetch, calls } = recordingFetch(201);

  const result = await uploadSourceMaps({ dir, url: "https://x", release: "v1", token: "t", fetch });

  assert.equal(result.uploaded, 1);
  assert.ok(calls[0].url.includes("filename=00d3wejgy8s0v.js"));
  assert.ok(calls[0].url.includes("debugId=aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));
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

test("a Next build's server output uploads through the server half, never under a bare file name", async () => {
  const dir = await buildFixture();
  await writeFile(join(dir, "BUILD_ID"), "b1");
  await mkdir(join(dir, "server", "chunks", "ssr"), { recursive: true });
  // The same file name in two directories, as Turbopack writes its runtime.
  for (const sub of ["chunks", "chunks/ssr"]) {
    await writeFile(join(dir, "server", sub, "[turbopack]_runtime.js"), "rt();\n//# sourceMappingURL=rt.js.map\n");
    await writeFile(join(dir, "server", sub, "rt.js.map"), '{"version":3,"sources":[],"mappings":"AAAA"}');
  }
  const { fetch, calls } = recordingFetch(201);

  const result = await uploadSourceMaps({ dir, url: "https://c.test", token: "t", release: "1", fetch });

  const filenames = calls.map((c) => new URL(c.url).searchParams.get("filename"));
  assert.ok(!filenames.includes("[turbopack]_runtime.js"));
  assert.ok(!filenames.includes("rt.js"));
  assert.deepEqual(
    filenames.filter((f) => f?.startsWith("server/")).sort(),
    ["server/chunks/[turbopack]_runtime.js", "server/chunks/ssr/[turbopack]_runtime.js"],
  );
  assert.ok(calls.filter((c) => c.url.includes("filename=server")).every((c) => c.url.includes("debugId=")));
  assert.equal(result.uploaded, calls.length);
});

test("another framework's server/ directory keeps the generic handling and is not stamped", async () => {
  const dir = await buildFixture();
  await mkdir(join(dir, "server"), { recursive: true });
  await writeFile(join(dir, "server", "entry.js"), "e();\n//# sourceMappingURL=entry.js.map\n");
  await writeFile(join(dir, "server", "entry.js.map"), '{"version":3,"sources":[],"mappings":"AAAA"}');
  const { fetch, calls } = recordingFetch(201);

  await uploadSourceMaps({ dir, url: "https://c.test", token: "t", release: "1", fetch });

  assert.ok(calls.some((c) => new URL(c.url).searchParams.get("filename") === "entry.js"));
  assert.ok(!(await readFile(join(dir, "server", "entry.js"), "utf8")).includes("debugId"));
});
