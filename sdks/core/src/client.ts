/**
 * The capture entry points. `init` stores the DSN + options; captureException / captureMessage build a
 * Sentry "store" event (event_id, timestamp, level, exception.values[]) and hand it to the resilient
 * transport, so the relay normalizes it exactly like an official Sentry SDK. Reporting never throws.
 */

import { parseDsn } from "./dsn.ts";
import { type SentryException, toException } from "./stack.ts";
import { sendEvent } from "./transport.ts";
import { type ConduxOptions, Level, type SendResult } from "./types.ts";

let options: ConduxOptions | undefined;

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
  if (!options) {
    throw new Error("Condux SDK not initialized, call init({ dsn }) first");
  }
  const { endpoint, projectId, publicKey } = parseDsn(options.dsn);
  const event = {
    event_id: globalThis.crypto.randomUUID().replace(/-/g, ""),
    timestamp: Date.now() / 1000, // epoch seconds, the Sentry store convention
    platform: "javascript",
    environment: options.environment,
    release: options.release,
    ...fields,
  };

  return sendEvent(
    `${endpoint}/api/${projectId}/store/`,
    { "content-type": "application/json", "x-condux-auth": publicKey },
    JSON.stringify(event),
    options,
  );
}
