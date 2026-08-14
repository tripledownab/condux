#!/usr/bin/env node
/**
 * Source-map upload for Condux (ADR-0028). Run after `next build` to upload the client source maps so the
 * control-plane can de-minify stack traces at read time. Bundler-agnostic (works for webpack and Turbopack
 * builds alike) because it just scans the build output for `.js.map` files rather than hooking the
 * bundler. Uploads each map to `POST /api/sourcemaps` keyed by (release, filename) with a scoped release
 * token — the same credential CI uses to record releases.
 *
 * ```bash
 * CONDUX_RELEASE_TOKEN=condux_rel_... \
 *   condux-sourcemaps --url https://app.condux.ai --release "$(git rev-parse HEAD)" --dir .next
 * ```
 *
 * Self-contained (Node built-ins only), so it needs no bundler plugin. `fetch` is injectable for tests.
 */

import { readdir, readFile } from "node:fs/promises";
import { basename, dirname, join } from "node:path";
import { pathToFileURL } from "node:url";

export interface UploadOptions {
  /** Build output directory to scan for source maps (a Next build is `.next`). */
  dir: string;
  /** The Condux app/API base URL, e.g. https://app.condux.ai. */
  url: string;
  /** A scoped release token (`condux_rel_...`). */
  token: string;
  /** The release version these maps belong to (match what the app reports as its release). */
  release: string;
  /** Optional distribution, when one release ships more than one build. */
  dist?: string;
  /** Injectable fetch (defaults to the global) so tests need no network. */
  fetch?: typeof fetch;
}

export interface UploadResult {
  uploaded: number;
  failed: number;
  files: Array<{ file: string; ok: boolean; status?: number; error?: string }>;
}

/** Every `*.js.map` under `dir`, recursively. */
export async function findSourceMaps(dir: string): Promise<string[]> {
  const entries = await readdir(dir, { recursive: true, withFileTypes: true });
  return entries
    .filter((entry) => entry.isFile() && entry.name.endsWith(".js.map"))
    .map((entry) => join(entry.parentPath, entry.name));
}

/** The chunk's trailing sourceMappingURL reference, when it carries one. Tail-anchored on purpose: the
 * same text inside a minified string literal must not count, and bundlers put the real pointer last. */
export function sourceMappingRef(js: string): string | undefined {
  return /\/\/# sourceMappingURL=(\S+)\s*$/.exec(js)?.[1];
}

export interface ChunkPair {
  jsPath: string;
  mapPath: string;
}

/**
 * Every built chunk under `dir` paired with its map by following the chunk's own sourceMappingURL
 * pointer. Name conventions cannot do this job: Turbopack hashes the map's filename independently of the
 * chunk's (`00d3wejgy8s0v.js` can point at `34hirxaxltle0.js.map`), so "strip .map" pairs nothing there,
 * while the pointer works for webpack and Turbopack alike.
 */
export async function findChunkPairs(dir: string): Promise<ChunkPair[]> {
  const entries = await readdir(dir, { recursive: true, withFileTypes: true });
  const files = new Set(
    entries.filter((entry) => entry.isFile()).map((entry) => join(entry.parentPath, entry.name)),
  );

  const pairs: ChunkPair[] = [];
  for (const jsPath of files) {
    if (!jsPath.endsWith(".js")) {
      continue;
    }
    const ref = sourceMappingRef(await readFile(jsPath, "utf8"));
    if (!ref) {
      continue;
    }
    const mapPath = join(dirname(jsPath), ref);
    if (files.has(mapPath)) {
      pairs.push({ jsPath, mapPath });
    }
  }
  return pairs;
}

/** One map upload, shared by this CLI and the withConduxConfig build plugin. Never throws. */
export async function uploadOneMap(options: {
  url: string;
  token: string;
  release: string;
  /** The built JS file the map is for (app.js, not app.js.map) — the release+path lookup key. */
  filename: string;
  dist?: string;
  /** The stable debugId the plugin injected, when there is one (the primary lookup key). */
  debugId?: string;
  body: string;
  fetch?: typeof fetch;
}): Promise<{ ok: boolean; status?: number; error?: string }> {
  const doFetch = options.fetch ?? fetch;
  const query = new URLSearchParams({ release: options.release, filename: options.filename });
  if (options.dist) {
    query.set("dist", options.dist);
  }
  if (options.debugId) {
    query.set("debugId", options.debugId);
  }

  try {
    const response = await doFetch(`${options.url.replace(/\/$/, "")}/api/sourcemaps?${query}`, {
      method: "POST",
      headers: { authorization: `Bearer ${options.token}`, "content-type": "application/json" },
      body: options.body,
    });
    return { ok: response.status >= 200 && response.status < 300, status: response.status };
  } catch (error) {
    return { ok: false, error: error instanceof Error ? error.message : String(error) };
  }
}

export async function uploadSourceMaps(options: UploadOptions): Promise<UploadResult> {
  const files: UploadResult["files"] = [];

  // Pointer-paired chunks first: the filename key must be the DEPLOYED chunk's name (what a stack
  // frame's abs_path ends in), which under Turbopack differs from the map's own name. The map's debugId
  // field rides along when the build plugin stamped one.
  const pairs = await findChunkPairs(options.dir);
  const claimed = new Set<string>();
  for (const { jsPath, mapPath } of pairs) {
    claimed.add(mapPath);
    const body = await readFile(mapPath, "utf8");
    const outcome = await uploadOneMap({
      url: options.url,
      token: options.token,
      release: options.release,
      filename: basename(jsPath),
      dist: options.dist,
      debugId: mapDebugId(body),
      body,
      fetch: options.fetch,
    });
    files.push({ file: mapPath, ...outcome });
  }

  // Any map no chunk points at (a webpack-style sibling whose chunk is elsewhere, or a stale build
  // leftover) still uploads under its name-derived filename, the pre-pointer behavior.
  for (const mapPath of await findSourceMaps(options.dir)) {
    if (claimed.has(mapPath)) {
      continue;
    }
    const outcome = await uploadOneMap({
      url: options.url,
      token: options.token,
      release: options.release,
      // app.js.map -> app.js
      filename: basename(mapPath).replace(/\.map$/, ""),
      dist: options.dist,
      body: await readFile(mapPath, "utf8"),
      fetch: options.fetch,
    });
    files.push({ file: mapPath, ...outcome });
  }

  const uploaded = files.filter((f) => f.ok).length;
  return { uploaded, failed: files.length - uploaded, files };
}

/** The TC39 debugId a stamped map carries, when it does. Shared with the build plugin's orphan pass. */
export function mapDebugId(mapBody: string): string | undefined {
  try {
    const debugId = (JSON.parse(mapBody) as { debugId?: unknown }).debugId;
    return typeof debugId === "string" ? debugId : undefined;
  } catch {
    return undefined;
  }
}

function arg(args: string[], name: string): string | undefined {
  const i = args.indexOf(`--${name}`);
  return i >= 0 && i + 1 < args.length ? args[i + 1] : undefined;
}

async function main(): Promise<void> {
  const args = process.argv.slice(2);
  const url = arg(args, "url") ?? process.env.CONDUX_URL;
  const release = arg(args, "release") ?? process.env.CONDUX_RELEASE;
  const token = process.env.CONDUX_RELEASE_TOKEN;
  const dir = arg(args, "dir") ?? ".next";
  const dist = arg(args, "dist") ?? process.env.CONDUX_DIST;

  if (!url || !release || !token) {
    console.error(
      "condux-sourcemaps: set --url (or CONDUX_URL), --release (or CONDUX_RELEASE), and CONDUX_RELEASE_TOKEN.",
    );
    process.exit(2);
  }

  const result = await uploadSourceMaps({ dir, url, release, token, dist });
  console.log(`condux-sourcemaps: uploaded ${result.uploaded}/${result.files.length} source maps from ${dir}`);
  for (const failure of result.files.filter((f) => !f.ok)) {
    console.error(`  failed: ${failure.file} (${failure.error ?? failure.status})`);
  }

  process.exit(result.failed > 0 ? 1 : 0);
}

// Run as the `condux-sourcemaps` CLI, but stay importable (the guard keeps main() from firing on import).
if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  void main();
}
