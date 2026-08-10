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
import { basename, join } from "node:path";
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

export async function uploadSourceMaps(options: UploadOptions): Promise<UploadResult> {
  const doFetch = options.fetch ?? fetch;
  const base = options.url.replace(/\/$/, "");
  const maps = await findSourceMaps(options.dir);
  const files: UploadResult["files"] = [];

  for (const mapPath of maps) {
    // The built JS file the map is for (app.js.map -> app.js), used as the release+path lookup key.
    const filename = basename(mapPath).replace(/\.map$/, "");
    const query = new URLSearchParams({ release: options.release, filename });
    if (options.dist) {
      query.set("dist", options.dist);
    }

    try {
      const body = await readFile(mapPath, "utf8");
      const response = await doFetch(`${base}/api/sourcemaps?${query}`, {
        method: "POST",
        headers: { authorization: `Bearer ${options.token}`, "content-type": "application/json" },
        body,
      });
      files.push({ file: mapPath, ok: response.status >= 200 && response.status < 300, status: response.status });
    } catch (error) {
      files.push({ file: mapPath, ok: false, error: error instanceof Error ? error.message : String(error) });
    }
  }

  const uploaded = files.filter((f) => f.ok).length;
  return { uploaded, failed: files.length - uploaded, files };
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
