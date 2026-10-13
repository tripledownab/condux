#!/usr/bin/env node
/**
 * Source-map upload for Condux (ADR-0028). Run after `next build` to upload the source maps so the
 * control-plane can de-minify stack traces at read time. Bundler-agnostic (works for webpack and Turbopack
 * builds alike) because it just scans the build output for `.js.map` files rather than hooking the
 * bundler. Uploads each map to `POST /api/sourcemaps` keyed by (release, filename) with a scoped release
 * token, the same credential CI uses to record releases. A Next build's `server/` output goes to the
 * server half (server-build.ts) instead, which stamps any chunk the build plugin did not, so run this
 * before the build output is packaged: a server chunk stamped after that deploys without its debugId.
 *
 * ```bash
 * CONDUX_RELEASE_TOKEN=condux_rel_... \
 *   condux-sourcemaps --url https://app.condux.ai --release "$(git rev-parse HEAD)" --dir .next
 * ```
 *
 * Self-contained (Node built-ins only), so it needs no bundler plugin. `fetch` is injectable for tests.
 */

import { existsSync } from "node:fs";
import { readFile } from "node:fs/promises";
import { basename, join, sep } from "node:path";
import { pathToFileURL } from "node:url";
import { findChunkPairs, findSourceMaps, mapDebugId, uploadOneMap } from "./map-files.ts";
import { processServerBuild } from "./server-build.ts";

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
  /** The maps outside a Next build's server output. The server half reports only its counts. */
  files: Array<{ file: string; ok: boolean; status?: number; error?: string }>;
}

export async function uploadSourceMaps(options: UploadOptions): Promise<UploadResult> {
  const files: UploadResult["files"] = [];
  // A Next build's server output is the server half's to upload: its maps need the debugId, their path
  // inside the build and their sources rewritten, and uploaded here by bare name they would overwrite
  // that copy and let a frame match another chunk's map.
  // BUILD_ID is Next's own, so another framework's server/ directory keeps the generic handling.
  const serverDir = join(options.dir, "server");
  const hasServer = existsSync(serverDir) && existsSync(join(options.dir, "BUILD_ID"));
  const outsideServer = (path: string) => !hasServer || !path.startsWith(serverDir + sep);

  // Pointer-paired chunks first: the filename key must be the DEPLOYED chunk's name (what a stack
  // frame's abs_path ends in), which under Turbopack differs from the map's own name. The map's debugId
  // field rides along when the build plugin stamped one.
  const pairs = (await findChunkPairs(options.dir)).filter((pair) => outsideServer(pair.jsPath));
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
    if (claimed.has(mapPath) || !outsideServer(mapPath)) {
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

  const server = hasServer
    ? await processServerBuild({ distDir: options.dir, upload: options, fetch: options.fetch })
    : { uploaded: 0, failed: 0 };
  const uploaded = files.filter((f) => f.ok).length;
  return {
    uploaded: uploaded + server.uploaded,
    failed: files.length - uploaded + server.failed,
    files,
  };
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
  console.log(`condux-sourcemaps: uploaded ${result.uploaded} source maps from ${dir}, ${result.failed} failed`);
  for (const failure of result.files.filter((f) => !f.ok)) {
    console.error(`  failed: ${failure.file} (${failure.error ?? failure.status})`);
  }

  process.exit(result.failed > 0 ? 1 : 0);
}

// Run as the `condux-sourcemaps` CLI, but stay importable (the guard keeps main() from firing on import).
if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  void main();
}
