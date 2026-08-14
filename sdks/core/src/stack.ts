/**
 * Turn a thrown value into a Sentry exception with parsed V8 stack frames, and normalize bundled frame
 * paths (#115) so the relay's code mappings and grouping line up with the repo. Frames are oldest-first
 * (the crashing frame last), the order the relay's fingerprinter and issue detail expect.
 */

// A V8 stack frame line: "at fn (file:line:col)" or the anonymous "at file:line:col".
const STACK_FRAME_PATTERN =
  /^\s*at (?:(?<fn>.+?) \()?(?<file>.+?):(?<line>\d+):(?<col>\d+)\)?\s*$/;
const ANONYMOUS_FRAME = "<anonymous>";

interface SentryFrame {
  filename: string;
  function: string;
  lineno: number;
  colno: number;
  in_app: boolean;
  /** The raw frame path before normalization (a browser chunk's full URL). The server keys source-map
   * symbolication on it (ADR-0028), matching it against the debug_meta images' code_file. */
  abs_path?: string;
}

export interface SentryException {
  type: string;
  value: string;
  // How the exception was captured. A captureException call is a handled capture, so Sentry's default
  // type "generic" + handled true; this drives the "unhandled" badge.
  mechanism?: { type: string; handled: boolean };
  stacktrace?: { frames: SentryFrame[] };
}

export function toException(error: unknown, handled = true): SentryException {
  const mechanism = { type: "generic", handled };
  if (error instanceof Error) {
    const frames = parseStack(error.stack);
    const exception: SentryException = { type: error.name, value: error.message, mechanism };
    if (frames.length > 0) {
      exception.stacktrace = { frames };
    }
    return exception;
  }
  return { type: "Error", value: String(error), mechanism };
}

// Parse a V8 `error.stack` into frames, oldest-first (the crashing frame last).
function parseStack(stack: string | undefined): SentryFrame[] {
  if (!stack) {
    return [];
  }
  const frames: SentryFrame[] = [];
  for (const line of stack.split("\n")) {
    const match = STACK_FRAME_PATTERN.exec(line);
    if (!match?.groups) {
      continue;
    }
    const raw = match.groups.file;
    const filename = normalizeFramePath(raw);
    frames.push({
      filename,
      function: match.groups.fn ?? ANONYMOUS_FRAME,
      lineno: Number(match.groups.line),
      colno: Number(match.groups.col),
      in_app: isInApp(filename),
      abs_path: raw,
    });
  }
  frames.reverse();
  return frames;
}

/**
 * Clean a raw V8 frame path into a source-relative one when the runtime bundled it (#115). A bundled
 * Node app (notably a Next.js server) reports frames with loader/URL scheme prefixes and Next's webpack
 * "layer" markers (webpack-internal:///(rsc)/./src/app/route.ts, file:///app/src/x.js, [project]/src/x.ts)
 * which the relay's code mappings (repo path resolution) and grouping cannot line up with the repo.
 * Stripping the known, stable prefixes yields src/app/route.ts. Purely additive: a path with no
 * recognized prefix (a plain /app/src/x.js, a node: internal) is returned unchanged, so this can only
 * improve a bundled path, never break an already-clean one.
 */
export function normalizeFramePath(raw: string): string {
  // Drop a webpack/query cache-buster suffix ("...route.ts?abc123").
  let path = raw.split(/[?#]/)[0];
  // Loader / URL scheme prefixes from bundlers and Node's ESM loader.
  path = path
    .replace(/^webpack-internal:\/\/\//, "")
    .replace(/^webpack:\/\/[^/]*\//, "")
    .replace(/^file:\/\//, "");
  // Next.js webpack route-group "layer" markers and Turbopack's project/loader roots.
  path = path
    .replace(/^\((?:rsc|ssr|action-browser|app-pages-browser|middleware|instrument)\)\//, "")
    .replace(/^\[[^\]]+\]\//, "");
  // A leading "./" the bundler kept on a relative source path.
  return path.replace(/^\.\//, "");
}

/**
 * The raw file of a stack's innermost frame — for a registration snippet appended to a chunk, the chunk's
 * own URL. What the debug-id registry (ADR-0028) keys a chunk by; V8 format, like the parser above.
 */
export function firstRawFile(stack: string): string | undefined {
  for (const line of stack.split("\n")) {
    const match = STACK_FRAME_PATTERN.exec(line);
    if (match?.groups) {
      return match.groups.file;
    }
  }
  return undefined;
}

// Application frames drive grouping and the culprit; library and runtime frames are noise. Runs on the
// normalized path, so bundled framework frames (which normalize to node_modules/next paths) are excluded.
function isInApp(filename: string): boolean {
  return (
    !filename.includes("node_modules") &&
    !filename.startsWith("node:") &&
    !filename.includes("next/dist/")
  );
}
