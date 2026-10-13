/**
 * The build-output file handling both halves of the source-map upload share: finding maps, pairing each
 * chunk with its map, reading a stamped map's debugId, and uploading one map. Used by the build plugin
 * (build.ts, server-build.ts) and the condux-sourcemaps CLI (sourcemaps.ts).
 */

import { readdir, readFile } from "node:fs/promises";
import { dirname, join } from "node:path";

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

// The pointer is a URL, and Turbopack percent-encodes it: [root-of-the-server]__x._.js points at
// %5Broot-of-the-server%5D__x._.js.map. Read undecoded, the map is looked for under a name that does not
// exist and the chunk goes unpaired. A pointer that does not decode is taken as it stands.
function toFileName(ref: string): string {
  try {
    return decodeURIComponent(ref);
  } catch {
    return ref;
  }
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
    const mapPath = join(dirname(jsPath), toFileName(ref));
    if (files.has(mapPath)) {
      pairs.push({ jsPath, mapPath });
    }
  }
  return pairs;
}

/** One map upload, shared by the condux-sourcemaps CLI and the withConduxConfig build plugin. Never throws. */
export async function uploadOneMap(options: {
  url: string;
  token: string;
  release: string;
  /** The built JS file the map is for (app.js, not app.js.map): the release+path lookup key. */
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

/** The TC39 debugId a stamped map carries, when it does. Shared with the build plugin's orphan pass. */
export function mapDebugId(mapBody: string): string | undefined {
  try {
    const debugId = (JSON.parse(mapBody) as { debugId?: unknown }).debugId;
    return typeof debugId === "string" ? debugId : undefined;
  } catch {
    return undefined;
  }
}
