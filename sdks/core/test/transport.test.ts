import assert from "node:assert/strict";
import { test } from "node:test";
import { type FetchLike, type FetchResponse, sendEvent } from "../src/index.ts";

const ENDPOINT = "http://relay.test/api/1/store/";
const HEADERS = { "x-condux-auth": "devkey" };

// A fake response with a case-insensitive headers.get() shim.
function response(status: number, headers: Record<string, string> = {}): FetchResponse {
  const lower = new Map(Object.entries(headers).map(([key, value]) => [key.toLowerCase(), value]));
  return { status, headers: { get: (name) => lower.get(name.toLowerCase()) ?? null } };
}

// A fetch that replays a scripted sequence of responses (or throws, for a network error);
// past the end it repeats the last step. Records every call.
function scriptedFetch(steps: Array<FetchResponse | Error>) {
  const calls: Array<{ url: string; body: string }> = [];
  const fetch: FetchLike = async (url, init) => {
    calls.push({ url, body: init.body });
    const step = steps[Math.min(calls.length - 1, steps.length - 1)];
    if (step instanceof Error) {
      throw step;
    }
    return step;
  };
  return { fetch, calls };
}

// Records requested sleep durations instead of actually waiting.
function recordingSleep() {
  const delays: number[] = [];
  return { sleep: async (ms: number) => void delays.push(ms), delays };
}

test("delivers on first 2xx without sleeping", async () => {
  const { fetch, calls } = scriptedFetch([response(202)]);
  const { sleep, delays } = recordingSleep();

  const result = await sendEvent(ENDPOINT, HEADERS, "{}", { fetch, sleep });

  assert.equal(result.ok, true);
  assert.equal(result.attempts, 1);
  assert.equal(calls.length, 1);
  assert.deepEqual(delays, []);
});

test("retries a 429 honoring Retry-After, then succeeds", async () => {
  const { fetch, calls } = scriptedFetch([response(429, { "retry-after": "2" }), response(202)]);
  const { sleep, delays } = recordingSleep();

  const result = await sendEvent(ENDPOINT, HEADERS, "{}", { fetch, sleep });

  assert.equal(result.ok, true);
  assert.equal(result.attempts, 2);
  assert.equal(calls.length, 2);
  assert.deepEqual(delays, [2000]); // waited exactly Retry-After seconds
});

test("falls back to exponential backoff for a 429 with no Retry-After", async () => {
  const { fetch } = scriptedFetch([response(429), response(202)]);
  const { sleep, delays } = recordingSleep();

  const result = await sendEvent(ENDPOINT, HEADERS, "{}", { fetch, sleep });

  assert.equal(result.ok, true);
  assert.deepEqual(delays, [200]);
});

test("stops immediately on a non-retriable 4xx (e.g. 401)", async () => {
  const { fetch, calls } = scriptedFetch([response(401)]);
  const { sleep, delays } = recordingSleep();

  const result = await sendEvent(ENDPOINT, HEADERS, "{}", { fetch, sleep, maxRetries: 3 });

  assert.equal(result.ok, false);
  assert.equal(result.status, 401);
  assert.equal(calls.length, 1); // did not retry
  assert.deepEqual(delays, []);
});

test("gives up after maxRetries on persistent 5xx, with exponential backoff", async () => {
  const { fetch, calls } = scriptedFetch([response(503)]);
  const { sleep, delays } = recordingSleep();

  const result = await sendEvent(ENDPOINT, HEADERS, "{}", { fetch, sleep, maxRetries: 2 });

  assert.equal(result.ok, false);
  assert.equal(result.status, 503);
  assert.equal(result.attempts, 3); // 1 initial + 2 retries
  assert.equal(calls.length, 3);
  assert.deepEqual(delays, [200, 400]); // slept between the 3 attempts
});

test("retries network errors and never throws", async () => {
  const { fetch, calls } = scriptedFetch([new Error("ECONNREFUSED")]);
  const { sleep } = recordingSleep();

  const result = await sendEvent(ENDPOINT, HEADERS, "{}", { fetch, sleep, maxRetries: 1 });

  assert.equal(result.ok, false);
  assert.equal(result.error, "ECONNREFUSED");
  assert.equal(calls.length, 2); // initial + 1 retry
});
