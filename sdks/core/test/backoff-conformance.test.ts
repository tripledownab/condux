import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { test } from "node:test";
import { type FetchLike, type FetchResponse, sendEvent } from "../src/index.ts";

// Drives sdks/conformance/backoff.tsv, the retry schedule every Condux SDK owes. The cases live in
// that file rather than here so the seven transports assert against one artifact instead of seven
// readings of one sentence in a comment. See the file for why it is data and not prose.

const ENDPOINT = "http://relay.test/api/1/store/";
const HEADERS = { "x-condux-auth": "devkey" };

export interface BackoffCase {
  attempt: number;
  status: number;
  retryAfter: string | null;
  expectedMs: number;
}

export function readBackoffCases(): BackoffCase[] {
  const text = readFileSync(new URL("../../conformance/backoff.tsv", import.meta.url), "utf8");
  const cases = text
    .split("\n")
    .map((line) => line.trim())
    .filter((line) => line !== "" && !line.startsWith("#"))
    .map((line) => {
      const [attempt, status, retryAfter, expectedMs] = line.split("\t");
      return {
        attempt: Number(attempt),
        status: Number(status),
        retryAfter: retryAfter === "<none>" ? null : retryAfter === "<empty>" ? "" : retryAfter,
        expectedMs: Number(expectedMs),
      };
    });
  // A fixture that failed to load reads exactly like one every case passed, so the count is asserted
  // rather than assumed. It only has to be a floor: adding a case must not mean editing seven SDKs.
  assert.ok(cases.length >= 15, `backoff.tsv looks truncated: ${cases.length} case(s)`);
  return cases;
}

function scriptedFetch(status: number, retryAfter: string | null): FetchLike {
  const headers = new Map(retryAfter === null ? [] : [["retry-after", retryAfter]]);
  const failure: FetchResponse = {
    status,
    headers: { get: (name) => headers.get(name.toLowerCase()) ?? null },
  };
  return async () => failure;
}

for (const { attempt, status, retryAfter, expectedMs } of readBackoffCases()) {
  const shown = retryAfter === null ? "no Retry-After" : `Retry-After: "${retryAfter}"`;
  test(`backoff conformance: attempt ${attempt}, ${status}, ${shown}`, async () => {
    const delays: number[] = [];
    // One more retry than the attempt under test, so the sleep that follows it is recorded. The
    // scripted response repeats, so every attempt fails and the schedule runs to its end.
    await sendEvent(ENDPOINT, HEADERS, "{}", {
      fetch: scriptedFetch(status, retryAfter),
      sleep: async (ms: number) => void delays.push(ms),
      maxRetries: attempt + 1,
    });

    assert.equal(delays.length, attempt + 1);
    assert.equal(delays[attempt], expectedMs);
  });
}
