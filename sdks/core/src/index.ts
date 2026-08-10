/**
 * Condux SDK core: the isomorphic engine shared by @condux/node, @condux/browser, @condux/edge and
 * @condux/nextjs. Emits the Sentry "store" wire shape (event_id, timestamp, level, exception.values[])
 * so the relay's parser normalizes it exactly like an official Sentry SDK: swap the DSN and it works.
 * fetch / crypto / URL only, no Node built-ins, so it runs unchanged in Node, browsers and edge runtimes.
 */

export { Level } from "./types.ts";
export type { ConduxOptions, FetchLike, FetchResponse, SendResult } from "./types.ts";
export { sendEvent } from "./transport.ts";
export { normalizeFramePath } from "./stack.ts";
export { captureException, captureMessage, init } from "./client.ts";
