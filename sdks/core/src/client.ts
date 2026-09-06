/**
 * The capture entry points. `init` stores the DSN + options; captureException / captureMessage build a
 * Sentry "store" event (event_id, timestamp, level, exception.values[]) and hand it to the resilient
 * transport, so the relay normalizes it exactly like an official Sentry SDK. Reporting never throws.
 */

import { type DebugIdImage, debugIdImages } from "./debug-ids.ts";
import { parseDsn } from "./dsn.ts";
import { modulesField } from "./modules.ts";
import { scopeFields } from "./scope.ts";
import { type SentryException, toException } from "./stack.ts";
import { sendEvent } from "./transport.ts";
import {
  type CaptureContext,
  type ConduxOptions,
  type ConduxRequest,
  Level,
  type SendResult,
} from "./types.ts";

let options: ConduxOptions | undefined;
let warnedUninitialized = false;

export function init(opts: ConduxOptions): void {
  options = opts;
}

/**
 * Report an exception as an error-level event, with its stack trace. Pass `handled: false` when
 * reporting an uncaught error (the browser/edge global handlers do this) so the relay marks it unhandled.
 * `context` carries per-event detail such as the request it happened during, which must not go through
 * the ambient scope on a server (see CaptureContext).
 */
export function captureException(
  error: unknown,
  handled = true,
  context: CaptureContext = {},
): Promise<SendResult> {
  return dispatch({ level: Level.Error, exception: { values: [toException(error, handled)] } }, context);
}

/** Report a bare message event at the given level (default info). */
export function captureMessage(
  message: string,
  level: Level = Level.Info,
  context: CaptureContext = {},
): Promise<SendResult> {
  return dispatch({ level, message }, context);
}

// The event fields that vary per capture (level plus a message or an exception).
type EventFields = {
  level: Level;
  message?: string;
  exception?: { values: SentryException[] };
};

function dispatch(fields: EventFields, context: CaptureContext = {}): Promise<SendResult> {
  // Reporting never throws — an error monitor that throws turns a handled error into an unhandled one
  // in exactly the code path where someone is already dealing with a failure. Warn once and no-op.
  if (!options) {
    if (!warnedUninitialized) {
      warnedUninitialized = true;
      console.warn("Condux: capture called before init({ dsn }); events are being dropped.");
    }
    return Promise.resolve({ ok: false, attempts: 0, error: "not_initialized" });
  }
  const { endpoint, projectId, publicKey } = parseDsn(options.dsn);
  const ambient = scopeFields();
  const event = {
    event_id: globalThis.crypto.randomUUID().replace(/-/g, ""),
    timestamp: Date.now() / 1000, // epoch seconds, the Sentry store convention
    platform: "javascript",
    environment: options.environment,
    release: options.release,
    ...ambient,
    // The loaded package versions (ADR-0041). Absent unless something declared them, which on a
    // browser or edge build is always, since neither has an installed tree to enumerate.
    ...modulesField(),
    // Per-event tags merge OVER the ambient ones rather than replacing the object, so setting a
    // request-scoped tag cannot silently drop the app's ambient tags.
    ...(context.tags !== undefined ? { tags: { ...ambient.tags, ...context.tags } } : {}),
    ...requestField(context),
    ...fields,
    ...debugMeta(fields),
  };

  // The tunnel (ADR-0028) is a same-origin forwarding route, so ad blockers that cut the monitoring
  // host cannot drop reports; the auth header still rides along for a route that chooses to reuse it.
  return sendEvent(
    options.tunnel ?? `${endpoint}/api/${projectId}/store/`,
    { "content-type": "application/json", "x-condux-auth": publicKey },
    JSON.stringify(event),
    options,
  );
}

/**
 * The event's `request`, which the relay parses into RequestInfo and the dashboard shows beside the
 * stack trace. Explicit detail from the caller wins; otherwise, in a browser, the page URL is filled in
 * from `location.href`.
 *
 * The auto fill lives here rather than in @condux/browser so that every browser event carries the page
 * it happened on, including manual captures and the ones from @condux/nextjs, which re-exports the core
 * entry points and cannot know at build time which runtime it will land in. Guarding on `location` is
 * what keeps the core isomorphic: Node leaves it undefined, so a server event is untouched.
 */
function requestField(context: CaptureContext): { request?: ConduxRequest } {
  const pageUrl = (globalThis as { location?: { href?: string } }).location?.href;
  const request: ConduxRequest = {
    ...(pageUrl !== undefined ? { url: pageUrl } : {}),
    ...context.request,
  };
  return Object.keys(request).length > 0 ? { request } : {};
}

// The debug_meta images for the event's frames (ADR-0028), keyed by the raw abs_path the server matches
// on. Present only when a build plugin registered debugIds and a frame matches one, so every other event
// keeps its exact wire shape.
function debugMeta(fields: EventFields): { debug_meta?: { images: DebugIdImage[] } } {
  const frames = fields.exception?.values.flatMap((value) => value.stacktrace?.frames ?? []) ?? [];
  const images = debugIdImages(frames.map((frame) => frame.abs_path ?? frame.filename));
  return images.length > 0 ? { debug_meta: { images } } : {};
}
