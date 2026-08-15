/**
 * Public options and shared value types for the Condux SDK core. `Level` is a const object rather than
 * an `enum` because the tests run under Node's type-stripping (erasure only, no `enum` codegen).
 */

export interface ConduxOptions {
  /** DSN, e.g. https://<publicKey>@relay.condux.ai/<projectId> */
  dsn: string;
  environment?: string;
  release?: string;
  /**
   * Send events to this URL (typically a same-origin path like "/monitoring") instead of the DSN's
   * ingest endpoint, so ad blockers that cut third-party monitoring hosts cannot drop reports. A server
   * route forwards the body to the relay (see conduxTunnelRoute in the Next.js SDK). The DSN is still
   * required — it names the project, and the forwarding route authenticates with its own copy.
   */
  tunnel?: string;
  /** Extra delivery attempts after the first (default 3, so up to 4 attempts). */
  maxRetries?: number;
  /** Advanced/testing hooks; default to the global fetch and real timers. */
  fetch?: FetchLike;
  sleep?: (ms: number) => Promise<void>;
}

/**
 * The request an event happened during. Field names are the Sentry store shape the relay parses into
 * RequestInfo, so they are snake_case on purpose and must stay that way.
 *
 * Deliberately no `headers`: they carry cookies and authorization, and while the relay scrubs sensitive
 * keys at ingest, that is a safety net rather than a reason to send them. Nothing needs them yet.
 */
export interface ConduxRequest {
  /** Path or absolute URL, without the query string. */
  url?: string;
  method?: string;
  query_string?: string;
}

/**
 * Per-event enrichment, passed at the capture call rather than set ambiently.
 *
 * This exists because the ambient scope (setUser / setTag) is module state, which is correct for a
 * browser tab and wrong for a server handling requests concurrently: two in-flight requests share it,
 * so one request's URL can ride another's event. A wrong URL is worse than none, because it sends
 * whoever is debugging to the wrong route. Anything derived from a single request belongs here.
 */
export interface CaptureContext {
  request?: ConduxRequest;
  /** Merged over the ambient scope's tags, so a per-event value wins for that key only. */
  tags?: Record<string, string>;
}

/** Event severity, matching the levels the relay understands. */
export const Level = {
  Debug: "debug",
  Info: "info",
  Warning: "warning",
  Error: "error",
  Fatal: "fatal",
} as const;
export type Level = (typeof Level)[keyof typeof Level];

/** The minimal slice of `fetch` the transport needs (so tests can supply a fake). */
export type FetchLike = (
  url: string,
  init: { method: string; headers: Record<string, string>; body: string },
) => Promise<FetchResponse>;

export interface FetchResponse {
  readonly status: number;
  readonly headers: { get(name: string): string | null };
}

/** Outcome of a delivery attempt sequence. Never throws, so inspect `ok`. */
export interface SendResult {
  readonly ok: boolean;
  readonly attempts: number;
  readonly status?: number;
  readonly error?: string;
}
