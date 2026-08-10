/**
 * Condux SDK for edge runtimes (Cloudflare Workers, Vercel Edge, Deno Deploy): report errors to a
 * Condux relay.
 *
 * The @condux/core engine is already isomorphic (fetch / crypto / URL only, no Node built-ins), so it runs
 * unchanged on the edge; this package re-exports it and adds `wrapFetch`, a boundary wrapper that reports
 * anything a fetch handler throws (as unhandled) and rethrows. Edge runtimes have no `window`, so there is
 * no global auto-capture: wrap the handler instead.
 */

export * from "@condux/core";

import { captureException } from "@condux/core";

/**
 * Wrap an edge/Worker fetch handler so any error it throws is reported to Condux (unhandled) and then
 * rethrown, so the runtime still surfaces it. Use at the boundary:
 *
 * ```ts
 * export default { fetch: wrapFetch(async (request) => handle(request)) };
 * ```
 */
export function wrapFetch<Args extends unknown[], R>(
  handler: (...args: Args) => R | Promise<R>,
): (...args: Args) => Promise<R> {
  return async (...args: Args) => {
    try {
      return await handler(...args);
    } catch (error) {
      await captureException(error, false);
      throw error;
    }
  };
}
