/**
 * Condux SDK for Next.js (App Router) — wire Condux into a Next app across all three runtimes with a few
 * lines. Thin glue over the SDKs it depends on: `@condux/core` (server + edge; the core is isomorphic, so
 * one init serves both the nodejs and edge runtimes) and `@condux/browser` (client, with global handlers).
 *
 * ```ts
 * // instrumentation.ts
 * export { register } from "@condux/nextjs";
 * export { captureRequestError as onRequestError } from "@condux/nextjs";
 *
 * // instrumentation-client.ts
 * import { initClient } from "@condux/nextjs";
 * initClient();
 * ```
 */

import { type ConduxOptions, captureException, init as initServer } from "@condux/core";
import { type BrowserOptions, init as initBrowser } from "@condux/browser";

export { captureException, captureMessage, Level, normalizeFramePath } from "@condux/core";
export type { ConduxOptions, FetchLike, FetchResponse, SendResult } from "@condux/core";

/**
 * Initialize server-side reporting. Call from `register()` in instrumentation.ts (Next runs it on the
 * Node and Edge runtimes). Reads the DSN from options or `CONDUX_DSN`; stays inert if neither is set, so
 * it never crashes a build that has not been configured yet.
 */
let serverInitialized = false;

export function register(options: Partial<ConduxOptions> = {}): void {
  const dsn = options.dsn ?? process.env.CONDUX_DSN;
  if (!dsn) {
    return;
  }
  initServer({
    ...options,
    dsn,
    environment: options.environment ?? process.env.NODE_ENV,
    release: options.release ?? process.env.CONDUX_RELEASE,
  });
  serverInitialized = true;
}

/**
 * Next's `onRequestError` hook: reports every uncaught server error (Server Components, route handlers,
 * server actions, middleware) as **unhandled**. Wire it up with
 * `export const onRequestError = captureRequestError`. The extra Next args (request, context) are accepted
 * for signature compatibility and currently unused.
 */
export async function captureRequestError(
  error: unknown,
  _request?: unknown,
  _context?: unknown,
): Promise<void> {
  // Next fires onRequestError regardless of configuration; stay silent (never throw) until register ran.
  if (!serverInitialized) {
    return;
  }
  await captureException(error, false);
}

/**
 * Initialize client-side reporting and the global browser handlers (uncaught errors + unhandled
 * rejections). Call from instrumentation-client.ts. Reads the DSN from options or the client-exposed
 * `NEXT_PUBLIC_CONDUX_DSN`; inert if neither is set.
 */
export function initClient(options: Partial<BrowserOptions> = {}): void {
  const dsn = options.dsn ?? process.env.NEXT_PUBLIC_CONDUX_DSN;
  if (!dsn) {
    return;
  }
  initBrowser({
    ...options,
    dsn,
    environment: options.environment ?? process.env.NODE_ENV,
    release: options.release ?? process.env.NEXT_PUBLIC_CONDUX_RELEASE,
  });
}
