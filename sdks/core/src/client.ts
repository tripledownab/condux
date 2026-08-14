/**
 * The capture entry points. `init` stores the DSN + options; captureException / captureMessage build a
 * Sentry "store" event (event_id, timestamp, level, exception.values[]) and hand it to the resilient
 * transport, so the relay normalizes it exactly like an official Sentry SDK. Reporting never throws.
 */

import { type DebugIdImage, debugIdImages } from "./debug-ids.ts";
import { parseDsn } from "./dsn.ts";
import { scopeFields } from "./scope.ts";
import { type SentryException, toException } from "./stack.ts";
import { sendEvent } from "./transport.ts";
import { type ConduxOptions, Level, type SendResult } from "./types.ts";

let options: ConduxOptions | undefined;
let warnedUninitialized = false;

export function init(opts: ConduxOptions): void {
  options = opts;
}

/**
 * Report an exception as an error-level event, with its stack trace. Pass `handled: false` when
 * reporting an uncaught error (the browser/edge global handlers do this) so the relay marks it unhandled.
 */
export function captureException(error: unknown, handled = true): Promise<SendResult> {
  return dispatch({ level: Level.Error, exception: { values: [toException(error, handled)] } });
}

/** Report a bare message event at the given level (default info). */
export function captureMessage(message: string, level: Level = Level.Info): Promise<SendResult> {
  return dispatch({ level, message });
}

// The event fields that vary per capture (level plus a message or an exception).
type EventFields = {
  level: Level;
  message?: string;
  exception?: { values: SentryException[] };
};

function dispatch(fields: EventFields): Promise<SendResult> {
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
  const event = {
    event_id: globalThis.crypto.randomUUID().replace(/-/g, ""),
    timestamp: Date.now() / 1000, // epoch seconds, the Sentry store convention
    platform: "javascript",
    environment: options.environment,
    release: options.release,
    ...scopeFields(),
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

// The debug_meta images for the event's frames (ADR-0028), keyed by the raw abs_path the server matches
// on. Present only when a build plugin registered debugIds and a frame matches one, so every other event
// keeps its exact wire shape.
function debugMeta(fields: EventFields): { debug_meta?: { images: DebugIdImage[] } } {
  const frames = fields.exception?.values.flatMap((value) => value.stacktrace?.frames ?? []) ?? [];
  const images = debugIdImages(frames.map((frame) => frame.abs_path ?? frame.filename));
  return images.length > 0 ? { debug_meta: { images } } : {};
}
