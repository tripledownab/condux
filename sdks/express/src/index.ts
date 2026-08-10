/**
 * Condux SDK for Express — report errors to a Condux relay.
 *
 * A thin adapter over the isomorphic `@condux/node` core: an error-handling middleware that reports every
 * error reaching Express's error pipeline (as unhandled, since it escaped the route) and passes it on, so
 * your own error handling still runs. Configure delivery with the core `init`, then register the handler
 * last. Single-file on purpose: the tests run the `.ts` source under Node's type-stripping, which the plain
 * `tsc` build also emits, and neither tolerates a relative `.ts` import.
 */

export * from "@condux/node";

import { captureException } from "@condux/node";

/** Express's `next` callback: forwards to the next (error) middleware, or the default handler. */
type Next = (err?: unknown) => void;

/**
 * Express error-handling middleware. Register it **after your routes**, so it catches anything they throw
 * or pass to `next(err)`:
 *
 * ```ts
 * app.use(conduxErrorHandler());
 * ```
 *
 * The returned function declares four parameters, which is how Express recognizes error middleware.
 */
export function conduxErrorHandler(): (err: unknown, req: unknown, res: unknown, next: Next) => void {
  return (err, _req, _res, next) => {
    void captureException(err, false);
    next(err);
  };
}
