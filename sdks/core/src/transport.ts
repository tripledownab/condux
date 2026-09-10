/**
 * Resilient event delivery. Transient failures (429 rate limits, 5xx, network errors) are retried with
 * capped exponential backoff, honoring the relay's Retry-After on a 429. Reporting never throws, so a
 * failed send cannot crash the host app. `fetch` and `sleep` are injectable so backoff is exercised with
 * no real timers. Inspired by common SDK transports, implemented fresh.
 */

import type { ConduxOptions, FetchLike, FetchResponse, SendResult } from "./types.ts";

const DEFAULT_MAX_RETRIES = 3;
const BASE_BACKOFF_MS = 200;
const MAX_BACKOFF_MS = 30_000;

/**
 * POST a serialized event to the relay, retrying transient failures with backoff.
 * Never throws, so returns a {@link SendResult} describing the outcome.
 */
export async function sendEvent(
  url: string,
  headers: Record<string, string>,
  body: string,
  transport: Pick<ConduxOptions, "maxRetries" | "fetch" | "sleep"> = {},
): Promise<SendResult> {
  const maxRetries = transport.maxRetries ?? DEFAULT_MAX_RETRIES;
  const doFetch = transport.fetch ?? (globalThis.fetch as unknown as FetchLike);
  const sleep = transport.sleep ?? defaultSleep;

  let lastStatus: number | undefined;
  let lastError: string | undefined;

  for (let attempt = 0; attempt <= maxRetries; attempt++) {
    let response: FetchResponse | undefined;
    try {
      response = await doFetch(url, { method: "POST", headers, body });
    } catch (cause) {
      lastError = cause instanceof Error ? cause.message : String(cause);
    }

    if (response) {
      lastStatus = response.status;
      lastError = undefined;
      if (response.status >= 200 && response.status < 300) {
        return { ok: true, attempts: attempt + 1, status: response.status };
      }
      if (!isRetriable(response.status)) {
        return { ok: false, attempts: attempt + 1, status: response.status };
      }
    }

    // Transient failure (429 / 5xx / network). Stop if that was the final attempt.
    if (attempt === maxRetries) {
      break;
    }
    await sleep(backoffMs(attempt, response));
  }

  return { ok: false, attempts: maxRetries + 1, status: lastStatus, error: lastError };
}

// 429 (rate limited) and 5xx are worth retrying; other 4xx (bad DSN, bad payload) are not.
function isRetriable(status: number): boolean {
  return status === 429 || status >= 500;
}

// Honor Retry-After (seconds) on a 429, else exponential backoff. Both paths are capped at
// MAX_BACKOFF_MS, at ONE return so a later branch cannot route past it: the SDK holds the caller's
// thread while it waits, so bounding that wait is its own obligation and not the relay's to set.
function backoffMs(attempt: number, response: FetchResponse | undefined): number {
  const requested =
    response?.status === 429 ? parseRetryAfterMs(response.headers.get("retry-after")) : undefined;
  return Math.min(requested ?? BASE_BACKOFF_MS * 2 ** attempt, MAX_BACKOFF_MS);
}

function parseRetryAfterMs(value: string | null): number | undefined {
  if (value === null || value.trim() === "") {
    return undefined;
  }
  const seconds = Number(value);
  return Number.isFinite(seconds) ? Math.max(0, seconds) * 1000 : undefined;
}

const defaultSleep = (ms: number): Promise<void> =>
  new Promise((resolve) => setTimeout(resolve, ms));
