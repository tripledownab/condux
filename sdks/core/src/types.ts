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
