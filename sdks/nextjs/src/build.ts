/**
 * The build-output half of withConduxConfig (ADR-0028): after the production compile, stamp each emitted
 * client chunk with a stable debugId, upload its source map to Condux, and drop uploaded maps from the
 * deployable output. Pure file processing over the build directory — chunks pair with their maps by
 * following each chunk's own sourceMappingURL pointer, which is what makes one implementation serve
 * webpack and Turbopack builds alike (Turbopack hashes map names independently of chunk names).
 */

import { createHash } from "node:crypto";
import { readFile, unlink, writeFile } from "node:fs/promises";
import { basename, join } from "node:path";
import { findChunkPairs, findSourceMaps, mapDebugId, sourceMappingRef, uploadOneMap } from "./sourcemaps.ts";

/** Where uploads go, resolved by the config wrapper. Absent means "do not upload". */
export interface UploadTarget {
  url: string;
  token: string;
  release: string;
  dist?: string;
}

export interface ProcessResult {
  stamped: number;
  uploaded: number;
  failed: number;
  /** Maps left on disk because they were not (successfully) uploaded — the CLI can still pick them up. */
  kept: number;
}

// The stamp's own marker doubles as the idempotence guard: a chunk containing it was already processed,
// and re-stamping would derive a different id from the now-changed bytes.
const REGISTRY_MARKER = "_conduxDebugIds";

/**
 * A deterministic, UUID-shaped debugId from the chunk's own bytes: the same build output always yields
 * the same id, so re-running a build or a deploy re-uploads under the same key instead of orphaning the
 * previous artifact.
 */
export function debugIdFromContent(content: string): string {
  const hex = createHash("sha256").update(content).digest("hex");
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20, 32)}`;
}

/**
 * The registration snippet appended to a chunk. It records the debugId keyed by a stack captured inside
 * the chunk — the one way a script can learn its own URL that works for every load path (async chunks,
 * module workers, injected tags) — and the SDK core resolves that stack back to the URL at capture time.
 * Appended, never prepended, so the map's existing line/column mappings stay valid.
 */
function debugIdSnippet(debugId: string): string {
  return (
    ';!function(){try{var g=typeof globalThis<"u"?globalThis:self;' +
    `g.${REGISTRY_MARKER}=g.${REGISTRY_MARKER}||{};var s=(new Error).stack;` +
    `s&&(g.${REGISTRY_MARKER}[s]="${debugId}")}catch(e){}}();`
  );
}

/**
 * Stamp the chunk: registration snippet + the standard debugId comment. The sourceMappingURL pointer is
 * re-appended (last, where tooling expects it) only while the map still exists beside the chunk —
 * once the map is uploaded and removed, a dangling pointer would just make browser devtools 404.
 */
export function stampChunk(js: string, debugId: string, mapRef?: string): string {
  const body = js.replace(/\n?\/\/# sourceMappingURL=\S+\s*$/, "\n").replace(/\n?$/, "\n");
  const pointer = mapRef ? `//# sourceMappingURL=${mapRef}\n` : "";
  return `${body}${debugIdSnippet(debugId)}\n//# debugId=${debugId}\n${pointer}`;
}

/**
 * Process the client half of a finished production build: for every chunk + map pair under the build's
 * `static/` output (exactly what deploys to the browser), compute the debugId from the chunk, stamp the
 * chunk, write the id into the map (the TC39 debugId field), upload the map, and delete it from the
 * output on a successful upload. Without an upload target (or on a failed upload) the map and the
 * chunk's pointer to it survive, so a later CI step (the condux-sourcemaps CLI) can still pair and
 * upload them — a map is never destroyed before it is stored somewhere. Idempotent (an already-stamped
 * chunk is skipped) and never throws: a build must not fail over monitoring plumbing.
 */
export async function processClientBuild(options: {
  /** The build output directory (a Next build's `.next`), absolute. */
  distDir: string;
  upload?: UploadTarget;
  fetch?: typeof fetch;
  log?: (message: string) => void;
}): Promise<ProcessResult> {
  const log = options.log ?? console.log;
  const result: ProcessResult = { stamped: 0, uploaded: 0, failed: 0, kept: 0 };

  let pairs: Awaited<ReturnType<typeof findChunkPairs>>;
  try {
    pairs = await findChunkPairs(join(options.distDir, "static"));
  } catch {
    // No static output (a server-only build target): nothing to do is a fine outcome.
    return result;
  }

  for (const { jsPath, mapPath } of pairs) {
    try {
      const js = await readFile(jsPath, "utf8");
      if (js.includes(REGISTRY_MARKER)) {
        continue;
      }
      const debugId = debugIdFromContent(js);
      const mapRef = sourceMappingRef(js);

      const map = JSON.parse(await readFile(mapPath, "utf8")) as Record<string, unknown>;
      map.debugId = debugId;
      const mapBody = JSON.stringify(map);
      await writeFile(mapPath, mapBody);

      const outcome = options.upload
        ? await uploadOneMap({
            ...options.upload,
            filename: basename(jsPath),
            debugId,
            body: mapBody,
            fetch: options.fetch,
          })
        : undefined;

      // The chunk keeps its map pointer unless the map is gone; the delete is last, after everything
      // that could fail has succeeded.
      await writeFile(jsPath, stampChunk(js, debugId, outcome?.ok ? undefined : mapRef));
      result.stamped++;
      if (outcome?.ok) {
        await unlink(mapPath);
        result.uploaded++;
      } else {
        result.kept++;
        if (outcome) {
          result.failed++;
          log(
            `condux: source map upload failed for ${basename(jsPath)} (${outcome.error ?? outcome.status})`,
          );
        }
      }
    } catch (error) {
      result.failed++;
      log(`condux: could not process ${jsPath}: ${error instanceof Error ? error.message : String(error)}`);
    }
  }

  // Orphan maps — ones no chunk points at (a pointer-less chunk's map, or a stale build leftover) —
  // cannot be stamped, but leaving them in the output serves them publicly like any static file. With
  // an upload target they upload under their name-derived filename (the same fallback key the CLI
  // uses) and leave the output; without one they stay untouched, same as before.
  if (options.upload) {
    const claimed = new Set(pairs.map((pair) => pair.mapPath));
    for (const mapPath of await findSourceMaps(join(options.distDir, "static"))) {
      if (claimed.has(mapPath)) {
        continue;
      }
      try {
        const body = await readFile(mapPath, "utf8");
        const outcome = await uploadOneMap({
          ...options.upload,
          filename: basename(mapPath).replace(/\.map$/, ""),
          debugId: mapDebugId(body),
          body,
          fetch: options.fetch,
        });
        if (outcome.ok) {
          await unlink(mapPath);
          result.uploaded++;
        } else {
          result.failed++;
          result.kept++;
          log(`condux: orphan map upload failed for ${basename(mapPath)} (${outcome.error ?? outcome.status})`);
        }
      } catch (error) {
        result.failed++;
        log(
          `condux: could not process orphan map ${mapPath}: ${error instanceof Error ? error.message : String(error)}`,
        );
      }
    }
  }

  if (result.stamped > 0 || result.uploaded > 0 || result.failed > 0) {
    log(
      `condux: stamped ${result.stamped} chunks with debugIds` +
        (options.upload
          ? `, uploaded ${result.uploaded} source maps${result.kept > 0 ? ` (${result.kept} kept on disk)` : ""}`
          : `; no upload target configured, ${result.kept} maps kept on disk — they WILL DEPLOY with ` +
            "the app (publicly, with original source) unless a later step uploads and removes them"),
    );
  }
  return result;
}
