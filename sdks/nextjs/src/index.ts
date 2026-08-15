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

import {
  type ConduxOptions,
  type ConduxRequest,
  captureException,
  init as initServer,
  parseDsn,
} from "@condux/core";
import { type BrowserOptions, init as initBrowser } from "@condux/browser";

export {
  addBreadcrumb,
  captureException,
  captureMessage,
  clearScope,
  setContext,
  setTag,
  setUser,
  Level,
  normalizeFramePath,
} from "@condux/core";
export type {
  Breadcrumb,
  CaptureContext,
  ConduxOptions,
  ConduxRequest,
  ConduxUser,
  FetchLike,
  FetchResponse,
  SendResult,
} from "@condux/core";

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

/** The `request` Next passes to onRequestError. Headers are deliberately not read (see below). */
interface NextRequest {
  path?: string;
  method?: string;
}

/** The `context` Next passes to onRequestError, narrowed to the fields worth reporting. */
interface NextErrorContext {
  routerKind?: string;
  routePath?: string;
  routeType?: string;
}

/**
 * Next's `onRequestError` hook: reports every uncaught server error (Server Components, route handlers,
 * server actions, middleware) as **unhandled**. Wire it up with
 * `export const onRequestError = captureRequestError`.
 *
 * The request and context Next supplies ride along, so a server event says which URL and route failed
 * instead of only what threw. They are passed per event rather than through setTag / setContext: the
 * ambient scope is module state, and a server handles requests concurrently, so one request's route
 * would end up on another request's event.
 *
 * `request.headers` is available here and deliberately ignored. It carries cookies and authorization,
 * and while the relay scrubs sensitive keys at ingest, not sending them at all is the stronger
 * guarantee. `routePath` is the parameterised form (/app/blog/[dynamic]), which is what makes it worth
 * a tag: it groups every dynamic instance of a route together.
 */
export async function captureRequestError(
  error: unknown,
  request?: NextRequest,
  context?: NextErrorContext,
): Promise<void> {
  // Next fires onRequestError regardless of configuration; stay silent (never throw) until register ran.
  if (!serverInitialized) {
    return;
  }
  await captureException(error, false, {
    ...requestFields(request),
    ...routeTags(context),
  });
}

/**
 * Next's `path` is a resource path that may carry the query string ("/blog?name=foo"), while the wire
 * shape keeps the two apart, so split rather than reporting a URL with a query glued on.
 */
function requestFields(request?: NextRequest): { request?: ConduxRequest } {
  if (request?.path === undefined && request?.method === undefined) {
    return {};
  }
  const [url, queryString] = (request.path ?? "").split("?", 2);
  return {
    request: {
      ...(url ? { url } : {}),
      ...(request.method !== undefined ? { method: request.method } : {}),
      ...(queryString ? { query_string: queryString } : {}),
    },
  };
}

/** Route detail as tags, so the dashboard can facet errors by route rather than only display them. */
function routeTags(context?: NextErrorContext): { tags?: Record<string, string> } {
  const tags = {
    ...(context?.routePath !== undefined ? { route: context.routePath } : {}),
    ...(context?.routeType !== undefined ? { route_type: context.routeType } : {}),
    ...(context?.routerKind !== undefined ? { router: context.routerKind } : {}),
  };
  return Object.keys(tags).length > 0 ? { tags } : {};
}

/**
 * Initialize client-side reporting and the global browser handlers (uncaught errors + unhandled
 * rejections). Call from instrumentation-client.ts, passing the DSN EXPLICITLY:
 * `initClient({ dsn: process.env.NEXT_PUBLIC_CONDUX_DSN })` — Next inlines NEXT_PUBLIC_* only where
 * application code references the variable, so this package's own env fallback resolves only when the
 * app names it. Warns and stays inert when no DSN resolves. Global handlers cover uncaught errors and
 * rejections; React RENDER errors go to error boundaries instead, so wrap the app in the re-exported
 * ConduxErrorBoundary to cover them.
 */
export function initClient(options: Partial<BrowserOptions> = {}): void {
  const dsn = options.dsn ?? process.env.NEXT_PUBLIC_CONDUX_DSN;
  if (!dsn) {
    // Loud, not silent: the env fallback CANNOT work unless the app itself references
    // NEXT_PUBLIC_CONDUX_DSN (Next inlines NEXT_PUBLIC_* only where application code names the
    // variable — inside this compiled package it stays undefined). Silent here made a misconfigured
    // app indistinguishable from a healthy one with no errors.
    console.warn(
      "Condux: initClient has no DSN, client reporting is OFF. Pass { dsn: process.env.NEXT_PUBLIC_CONDUX_DSN } "
        + "explicitly from instrumentation-client.ts so Next inlines the variable into your bundle.",
    );
    return;
  }
  initBrowser({
    ...options,
    dsn,
    environment: options.environment ?? process.env.NODE_ENV,
    release: options.release ?? process.env.NEXT_PUBLIC_CONDUX_RELEASE,
  });
}

// Events are small JSON; anything past this is not one of ours and is refused before it is forwarded.
const TUNNEL_MAX_BODY_BYTES = 1024 * 1024;

/**
 * The ad-blocker tunnel (ADR-0028 slice 5): a same-origin route that forwards browser events to the
 * relay, so an extension that cuts third-party monitoring hosts cannot drop reports. Pair it with the
 * client's `tunnel` option:
 *
 * ```ts
 * // app/monitoring/route.ts
 * import { conduxTunnelRoute } from "@condux/nextjs";
 * export const POST = conduxTunnelRoute();
 *
 * // instrumentation-client.ts
 * initClient({ tunnel: "/monitoring" });
 * ```
 *
 * The route authenticates with its own DSN (`CONDUX_DSN`, falling back to the public one), never with
 * anything the browser sent — so it can only ever report into this app's project, and abusing it is
 * exactly as possible as using the public DSN directly. Body size is capped; the relay's own rate limit
 * and quota still apply behind it.
 */
export function conduxTunnelRoute(
  options: { dsn?: string; fetch?: typeof fetch } = {},
): (request: Request) => Promise<Response> {
  const doFetch = options.fetch ?? fetch;
  return async (request: Request): Promise<Response> => {
    const dsn = options.dsn ?? process.env.CONDUX_DSN ?? process.env.NEXT_PUBLIC_CONDUX_DSN;
    if (!dsn) {
      return Response.json({ error: "tunnel_not_configured" }, { status: 503 });
    }

    const body = await request.text();
    if (body.length > TUNNEL_MAX_BODY_BYTES) {
      return new Response(null, { status: 413 });
    }

    const { endpoint, projectId, publicKey } = parseDsn(dsn);
    const relayResponse = await doFetch(`${endpoint}/api/${projectId}/store/`, {
      method: "POST",
      headers: { "content-type": "application/json", "x-condux-auth": publicKey },
      body,
    });
    // Status passthrough: the browser SDK's transport reads it to decide retries (429/5xx), so hiding a
    // relay refusal here would turn every failure into a silent success.
    return new Response(await relayResponse.text(), { status: relayResponse.status });
  };
}
