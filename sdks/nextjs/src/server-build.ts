/**
 * The server half of withConduxConfig's build processing (ADR-0028): stamp each emitted server chunk
 * with a debugId and upload its source map, so a server error symbolicates like a client one. Next's
 * server runs the bundle, not the source, and `next start` does not map error stacks, so without this a
 * server frame names a chunk no repository holds.
 *
 * Where it differs from the client half: the maps stay on disk, since nothing under `server/` is served
 * and Next reads them for its own error logs. A stamped chunk still uploads on a later pass, so the CLI
 * can upload a build stamped without credentials. A Turbopack map's sources are rewritten in the uploaded copy
 * (see toProjectSources). And a map is uploaded under its path inside the build, not its file name,
 * because server file names repeat across directories (a Turbopack build writes [turbopack]_runtime.js
 * in both chunks/ and chunks/ssr/): a frame's file name then never matches the release lookup, and only
 * the debugId resolves the map, which is the one key that cannot pick another chunk's map.
 */

import { existsSync } from "node:fs";
import { readFile, writeFile } from "node:fs/promises";
import { join, relative, sep } from "node:path";
import { type UploadTarget, debugIdFromContent, isStamped, stampChunk } from "./build.ts";
import { findChunkPairs, sourceMappingRef, uploadOneMap } from "./map-files.ts";

// Present in a Turbopack build's server output and absent from webpack's, which names its runtime
// webpack-runtime.js. Measured on a Next 16.3.4 build.
const TURBOPACK_RUNTIME = "[turbopack]_runtime.js";

// Turbopack writes a server map's sources as a run of ../ followed by the path from the workspace root,
// percent-encoded: ../../../../web/app/api/x.ts from .next/server/chunks. The run does not always end at
// the workspace root (an edge map's climbs to .next), but what follows it is the workspace path either
// way. Measured on a Next 16.3.4 build: every source from the app's own tree exists at the workspace
// root once the run is removed. The ones that do not are Next's virtual modules and dependency files.
// The client maps name the same file turbopack:///[project]/web/app/api/x.ts, which the server already
// turns into a repository path, so the uploaded copy says it that way. The copy on disk keeps its own
// sources, since Next resolves them for its error logs.
function toProjectSources(map: Record<string, unknown>): Record<string, unknown> {
  const rewrite = (source: unknown): unknown => {
    if (typeof source !== "string" || !source.startsWith("../")) {
      return source;
    }
    const rest = source.replace(/^(\.\.\/)+/, "");
    try {
      return `turbopack:///[project]/${decodeURIComponent(rest)}`;
    } catch {
      return `turbopack:///[project]/${rest}`;
    }
  };
  // An index map (`sections`) carries its sources inside each section's map, so each one is rewritten
  // the same way; the server reads index maps section by section.
  if (Array.isArray(map.sections)) {
    return {
      ...map,
      sections: map.sections.map((section: unknown) =>
        isObject(section) && isObject(section.map)
          ? { ...section, map: toProjectSources(section.map) }
          : section,
      ),
    };
  }
  return Array.isArray(map.sources) ? { ...map, sources: map.sources.map(rewrite) } : map;
}

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

export interface ServerProcessResult {
  stamped: number;
  uploaded: number;
  failed: number;
}

/**
 * Stamp every server chunk + map pair under the build's `server/` output that is not stamped yet, write
 * the debugId into the map, and upload every stamped map when a target is configured. Idempotent (a
 * stamped chunk is never re-stamped) and never throws: a build must not fail over monitoring plumbing.
 */
export async function processServerBuild(options: {
  /** The build output directory (a Next build's `.next`), absolute. */
  distDir: string;
  upload?: UploadTarget;
  fetch?: typeof fetch;
  log?: (message: string) => void;
}): Promise<ServerProcessResult> {
  const log = options.log ?? console.log;
  const result: ServerProcessResult = { stamped: 0, uploaded: 0, failed: 0 };

  let pairs: Awaited<ReturnType<typeof findChunkPairs>>;
  try {
    pairs = await findChunkPairs(join(options.distDir, "server"));
  } catch {
    // No server output (a static export): nothing to do is a fine outcome.
    return result;
  }

  const turbopack = existsSync(join(options.distDir, "server/chunks", TURBOPACK_RUNTIME));
  for (const { jsPath, mapPath } of pairs) {
    try {
      const js = await readFile(jsPath, "utf8");
      const map = JSON.parse(await readFile(mapPath, "utf8")) as Record<string, unknown>;
      // A chunk stamped by an earlier pass keeps its id, so a build stamped without credentials can be
      // uploaded later by the condux-sourcemaps CLI.
      if (!isStamped(js)) {
        map.debugId = debugIdFromContent(js);
        await writeFile(mapPath, JSON.stringify(map));
        await writeFile(jsPath, stampChunk(js, map.debugId as string, sourceMappingRef(js)));
        result.stamped++;
      }

      if (!options.upload) {
        continue;
      }
      const debugId = typeof map.debugId === "string" ? map.debugId : undefined;
      if (!debugId) {
        // Stamped, but another tool rewrote the map without the id, so nothing could resolve it.
        result.failed++;
        log(`condux: ${jsPath} is stamped but its map carries no debugId, so it was not uploaded`);
        continue;
      }
      const outcome = await uploadOneMap({
        ...options.upload,
        filename: relative(options.distDir, jsPath).split(sep).join("/"),
        debugId,
        body: JSON.stringify(turbopack ? toProjectSources(map) : map),
        fetch: options.fetch,
      });
      if (outcome.ok) {
        result.uploaded++;
      } else {
        result.failed++;
        log(`condux: server source map upload failed for ${jsPath} (${outcome.error ?? outcome.status})`);
      }
    } catch (error) {
      result.failed++;
      log(`condux: could not process ${jsPath}: ${error instanceof Error ? error.message : String(error)}`);
    }
  }

  if (result.stamped > 0 || result.uploaded > 0 || result.failed > 0) {
    log(
      `condux: stamped ${result.stamped} server chunks with debugIds` +
        (options.upload ? `, uploaded ${result.uploaded} server source maps` : "; no upload target configured"),
    );
  }
  return result;
}
