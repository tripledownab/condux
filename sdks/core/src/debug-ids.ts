/**
 * Debug-id support (ADR-0028). The withConduxConfig build plugin appends a snippet to each client chunk
 * that records the chunk's debugId in a global registry, keyed by a stack captured inside the chunk (the
 * only way a script can learn its own URL that works for every load path). At capture time this module
 * turns that registry into the Sentry debug_meta images the server's symbolicator keys on — one
 * { type: "sourcemap", code_file, debug_id } per frame file with a known id, code_file being the same raw
 * URL the frame carries as abs_path so the two match exactly.
 */

import { firstRawFile } from "./stack.ts";

const REGISTRY_KEY = "_conduxDebugIds";

export interface DebugIdImage {
  type: "sourcemap";
  code_file: string;
  debug_id: string;
}

/**
 * The debug_meta images for an event's frame files (raw abs_path values). Empty when no build plugin
 * registered anything — the ordinary case for every non-browser runtime, so this stays allocation-light
 * and never throws. The registry is small (one entry per loaded chunk) and captures are rare, so it is
 * re-read per event rather than cached.
 */
export function debugIdImages(frameFiles: Iterable<string>): DebugIdImage[] {
  const registry = (globalThis as Record<string, unknown>)[REGISTRY_KEY];
  if (registry === null || typeof registry !== "object") {
    return [];
  }

  const byFile = new Map<string, string>();
  for (const [stack, debugId] of Object.entries(registry as Record<string, string>)) {
    const file = typeof debugId === "string" ? firstRawFile(stack) : undefined;
    if (file) {
      byFile.set(file, debugId);
    }
  }

  const images: DebugIdImage[] = [];
  const seen = new Set<string>();
  for (const file of frameFiles) {
    const debugId = byFile.get(file);
    if (debugId && !seen.has(file)) {
      seen.add(file);
      images.push({ type: "sourcemap", code_file: file, debug_id: debugId });
    }
  }
  return images;
}
