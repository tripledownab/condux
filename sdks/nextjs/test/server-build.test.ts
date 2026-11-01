import assert from "node:assert/strict";
import { mkdir, mkdtemp, readFile, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { debugIdFromContent } from "../src/build.ts";
import { processServerBuild } from "../src/server-build.ts";

// Two chunks that share a file name in different directories, each pointing at its own map, as a
// Turbopack build's runtime does in chunks/ and chunks/ssr/.
const ROUTE_A = 'module.exports = "a";\n//# sourceMappingURL=page.js.map\n';
const ROUTE_B = 'module.exports = "b";\n//# sourceMappingURL=page.js.map\n';
const MAP = JSON.stringify({ version: 3, sources: ["turbopack:///[project]/web/app/x.ts"], mappings: "AAAA" });

async function serverBuild(): Promise<{ distDir: string; a: string; b: string }> {
  const distDir = await mkdtemp(join(tmpdir(), "condux-server-"));
  for (const route of ["orders", "users"]) {
    await mkdir(join(distDir, "server/app", route), { recursive: true });
    await writeFile(join(distDir, "server/app", route, "page.js.map"), MAP);
  }
  const a = join(distDir, "server/app/orders/page.js");
  const b = join(distDir, "server/app/users/page.js");
  await writeFile(a, ROUTE_A);
  await writeFile(b, ROUTE_B);
  return { distDir, a, b };
}

function recordingFetch(status = 200) {
  const calls: string[] = [];
  const doFetch = (async (url: RequestInfo | URL) => {
    calls.push(decodeURIComponent(String(url)));
    return new Response(null, { status });
  }) as typeof fetch;
  return { doFetch, calls };
}

const upload = { url: "https://condux.test", token: "condux_rel_x", release: "web@1" };

test("a server chunk is stamped and its map carries the same debugId, and both stay on disk", async () => {
  const { distDir, a } = await serverBuild();

  const result = await processServerBuild({ distDir, log: () => {} });

  const stamped = await readFile(a, "utf8");
  const id = debugIdFromContent(ROUTE_A);
  assert.ok(stamped.startsWith('module.exports = "a";'));
  assert.ok(stamped.includes(`_conduxDebugIds[s]="${id}"`));
  // Next reads the map for its own error logs, so the pointer survives even when nothing uploads.
  assert.ok(stamped.endsWith("//# sourceMappingURL=page.js.map\n"));
  assert.equal(JSON.parse(await readFile(join(distDir, "server/app/orders/page.js.map"), "utf8")).debugId, id);
  assert.deepEqual(result, { stamped: 2, uploaded: 0, failed: 0 });
});

test("each map uploads under its own path in the build, so two page.js never share a key", async () => {
  const { distDir, a } = await serverBuild();
  const { doFetch, calls } = recordingFetch();

  const result = await processServerBuild({ distDir, upload, fetch: doFetch, log: () => {} });

  assert.equal(result.uploaded, 2);
  const filenames = calls.map((url) => new URL(url).searchParams.get("filename")).sort();
  assert.deepEqual(filenames, ["server/app/orders/page.js", "server/app/users/page.js"]);
  assert.ok(calls.some((url) => url.includes(`debugId=${debugIdFromContent(ROUTE_A)}`)));
  // Uploading does not remove anything: the server output is not served.
  assert.ok((await readFile(a, "utf8")).endsWith("//# sourceMappingURL=page.js.map\n"));
});

test("a Turbopack build uploads its sources in the client maps' form and keeps its own on disk", async () => {
  const distDir = await mkdtemp(join(tmpdir(), "condux-server-"));
  await mkdir(join(distDir, "server/chunks"), { recursive: true });
  await writeFile(join(distDir, "server/chunks/[turbopack]_runtime.js"), "runtime();\n");
  const chunk = join(distDir, "server/chunks/app.js");
  const sources = ["../../../../web/app/api/boom/route.ts", "../../../../node_modules/.pnpm/next%4016.3.4/x.js", "turbopack:///[turbopack]/r.ts"];
  await writeFile(chunk, "boom();\n//# sourceMappingURL=app.js.map\n");
  await writeFile(`${chunk}.map`, JSON.stringify({ version: 3, sources, mappings: "AAAA" }));
  const { doFetch, calls } = recordingFetch();
  const bodies: string[] = [];
  const capture = (async (url: RequestInfo | URL, init?: RequestInit) => {
    bodies.push(String(init?.body));
    return doFetch(url, init);
  }) as typeof fetch;

  await processServerBuild({ distDir, upload, fetch: capture, log: () => {} });

  assert.equal(calls.length, 1);
  assert.deepEqual(JSON.parse(bodies[0]).sources, [
    "turbopack:///[project]/web/app/api/boom/route.ts",
    "turbopack:///[project]/node_modules/.pnpm/next@16.3.4/x.js",
    "turbopack:///[turbopack]/r.ts",
  ]);
  assert.deepEqual(JSON.parse(await readFile(`${chunk}.map`, "utf8")).sources, sources);
});

test("a Turbopack index map uploads each section's sources in the client maps' form", async () => {
  const distDir = await mkdtemp(join(tmpdir(), "condux-server-"));
  await mkdir(join(distDir, "server/chunks"), { recursive: true });
  await writeFile(join(distDir, "server/chunks/[turbopack]_runtime.js"), "runtime();\n");
  const chunk = join(distDir, "server/chunks/entry.js");
  const section = (line: number, source: string) => ({
    offset: { line, column: 0 },
    map: { version: 3, sources: [source], mappings: "AAAA" },
  });
  const map = {
    version: 3,
    sections: [section(0, "../../../../web/app/a.ts"), section(5, "../../../../web/app/b.ts")],
  };
  await writeFile(chunk, "a();\n//# sourceMappingURL=entry.js.map\n");
  await writeFile(`${chunk}.map`, JSON.stringify(map));
  const bodies: string[] = [];
  const capture = (async (_url: RequestInfo | URL, init?: RequestInit) => {
    bodies.push(String(init?.body));
    return new Response(null, { status: 200 });
  }) as typeof fetch;

  await processServerBuild({ distDir, upload, fetch: capture, log: () => {} });

  const uploaded = JSON.parse(bodies[0]);
  assert.deepEqual(
    uploaded.sections.map((s: { map: { sources: string[] } }) => s.map.sources[0]),
    ["turbopack:///[project]/web/app/a.ts", "turbopack:///[project]/web/app/b.ts"],
  );
  assert.deepEqual(uploaded.sections.map((s: { offset: { line: number } }) => s.offset.line), [0, 5]);
});

test("a build that is not Turbopack's uploads its sources as they are", async () => {
  const distDir = await mkdtemp(join(tmpdir(), "condux-server-"));
  await mkdir(join(distDir, "server/chunks"), { recursive: true });
  const chunk = join(distDir, "server/chunks/app.js");
  await writeFile(chunk, "boom();\n//# sourceMappingURL=app.js.map\n");
  await writeFile(`${chunk}.map`, JSON.stringify({ version: 3, sources: ["../src/x.ts"], mappings: "AAAA" }));
  const bodies: string[] = [];
  const capture = (async (_url: RequestInfo | URL, init?: RequestInit) => {
    bodies.push(String(init?.body));
    return new Response(null, { status: 200 });
  }) as typeof fetch;

  await processServerBuild({ distDir, upload, fetch: capture, log: () => {} });

  assert.deepEqual(JSON.parse(bodies[0]).sources, ["../src/x.ts"]);
});

test("a percent-encoded pointer still finds its map, as Turbopack writes them", async () => {
  const distDir = await mkdtemp(join(tmpdir(), "condux-server-"));
  await mkdir(join(distDir, "server/chunks"), { recursive: true });
  const chunk = join(distDir, "server/chunks/[root-of-the-server]__1ffbpxc._.js");
  await writeFile(chunk, "boom();\n//# sourceMappingURL=%5Broot-of-the-server%5D__1ffbpxc._.js.map\n");
  await writeFile(`${chunk}.map`, MAP);

  const result = await processServerBuild({ distDir, log: () => {} });

  assert.equal(result.stamped, 1);
  // The pointer is written back as the URL it was, not as the decoded file name.
  assert.ok((await readFile(chunk, "utf8")).endsWith("//# sourceMappingURL=%5Broot-of-the-server%5D__1ffbpxc._.js.map\n"));
});

test("a second pass leaves a stamped chunk alone", async () => {
  const { distDir, a } = await serverBuild();
  await processServerBuild({ distDir, log: () => {} });
  const once = await readFile(a, "utf8");

  const again = await processServerBuild({ distDir, log: () => {} });

  assert.equal(await readFile(a, "utf8"), once);
  assert.equal(again.stamped, 0);
});

test("a chunk that bundles the SDK is still stamped", async () => {
  const distDir = await mkdtemp(join(tmpdir(), "condux-server-"));
  await mkdir(join(distDir, "server/chunks"), { recursive: true });
  const chunk = join(distDir, "server/chunks/app.js");
  // @condux/core reads the registry by this name, so a bundle of it carries the string unstamped.
  await writeFile(chunk, 'var r=globalThis._conduxDebugIds;\n//# sourceMappingURL=app.js.map\n');
  await writeFile(`${chunk}.map`, MAP);

  assert.equal((await processServerBuild({ distDir, log: () => {} })).stamped, 1);
});

test("a debugId comment inlined from a dependency does not count as our stamp", async () => {
  const distDir = await mkdtemp(join(tmpdir(), "condux-server-"));
  await mkdir(join(distDir, "server/chunks"), { recursive: true });
  const chunk = join(distDir, "server/chunks/app.js");
  // A package published with debug ids of its own, bundled into the middle of the chunk.
  const inlined = "//# debugId=0f0e0d0c-0b0a-4908-8706-050403020100\n";
  await writeFile(chunk, `lib();\n${inlined}app();\n//# sourceMappingURL=app.js.map\n`);
  await writeFile(`${chunk}.map`, MAP);

  assert.equal((await processServerBuild({ distDir, log: () => {} })).stamped, 1);
});

test("a build stamped without credentials uploads on a later pass, under the id it was stamped with", async () => {
  const { distDir } = await serverBuild();
  await processServerBuild({ distDir, log: () => {} });
  const { doFetch, calls } = recordingFetch();
  const logs: string[] = [];

  const result = await processServerBuild({ distDir, upload, fetch: doFetch, log: (m) => logs.push(m) });

  assert.deepEqual(result, { stamped: 0, uploaded: 2, failed: 0 });
  // A pass that only uploads still says so in the build log.
  assert.ok(logs.some((line) => line.includes("uploaded 2 server source maps")));
  assert.ok(calls.some((url) => url.includes(`debugId=${debugIdFromContent(ROUTE_A)}`)));
});

test("a stamped chunk whose map lost its debugId is counted as failed, not silently skipped", async () => {
  const { distDir } = await serverBuild();
  await processServerBuild({ distDir, log: () => {} });
  await writeFile(join(distDir, "server/app/orders/page.js.map"), MAP);
  const logs: string[] = [];

  const result = await processServerBuild({ distDir, upload, fetch: recordingFetch().doFetch, log: (m) => logs.push(m) });

  assert.deepEqual(result, { stamped: 0, uploaded: 1, failed: 1 });
  assert.ok(logs.some((line) => line.includes("carries no debugId")));
});

test("a failed upload is counted and logged, never thrown into the build", async () => {
  const { distDir } = await serverBuild();
  // Refused rather than failed for now, which would be retried.
  const { doFetch } = recordingFetch(403);
  const logs: string[] = [];

  const result = await processServerBuild({ distDir, upload, fetch: doFetch, log: (m) => logs.push(m) });

  assert.deepEqual(result, { stamped: 2, uploaded: 0, failed: 2 });
  assert.ok(logs.some((line) => line.includes("upload failed")));
});

test("a build with no server output is a quiet no-op", async () => {
  const distDir = await mkdtemp(join(tmpdir(), "condux-static-"));

  assert.deepEqual(await processServerBuild({ distDir, log: () => {} }), { stamped: 0, uploaded: 0, failed: 0 });
});
