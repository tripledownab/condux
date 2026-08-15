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

/** The slice of an Express request this reads. Narrowed rather than typed as Express's own Request, so
 * the package keeps no dependency on express itself. */
interface ExpressRequest {
  originalUrl?: string;
  url?: string;
  method?: string;
}

/**
 * The request, in the Sentry store shape the relay parses.
 *
 * `originalUrl` rather than `url`, because a router mounted with `app.use("/api", router)` rewrites
 * `url` to the path within the router and only `originalUrl` still names the endpoint the client
 * actually called. The query string is split off, matching the wire shape.
 *
 * Headers are on the request and deliberately not read: they carry cookies and authorization, and while
 * the relay scrubs sensitive keys at ingest, not sending credentials is the stronger guarantee.
 */
function describe(req: unknown): { url?: string; method?: string; query_string?: string } {
  const request = (req ?? {}) as ExpressRequest;
  const target = request.originalUrl ?? request.url;
  const [path, queryString] = (target ?? "").split("?", 2);
  return {
    ...(path ? { url: path } : {}),
    ...(request.method ? { method: request.method } : {}),
    ...(queryString ? { query_string: queryString } : {}),
  };
}

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
  return (err, req, _res, next) => {
    // The request rides along per event rather than through setTag: the scope is module state, and a
    // server handles requests concurrently, so one request's URL would attach to another's event.
    const request = describe(req);
    void captureException(err, false, Object.keys(request).length > 0 ? { request } : {});
    next(err);
  };
}
