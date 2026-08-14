import assert from "node:assert/strict";
import { test } from "node:test";
import { conduxTunnelRoute } from "../src/index.ts";

const DSN = "https://pubkey123@ingest.condux.test/0198aaaa-0000-7000-8000-000000000001";
const EVENT = JSON.stringify({ event_id: "e".repeat(32), level: "error" });

function recordingFetch(status = 200, body = '{"id":"e"}') {
  const calls: { url: string; headers: Record<string, string>; body: string }[] = [];
  const doFetch = (async (url: RequestInfo | URL, init?: RequestInit) => {
    calls.push({
      url: String(url),
      headers: (init?.headers ?? {}) as Record<string, string>,
      body: String(init?.body),
    });
    return new Response(body, { status });
  }) as typeof fetch;
  return { doFetch, calls };
}

test("forwards the body to the relay's store endpoint with the route's own DSN auth", async () => {
  const { doFetch, calls } = recordingFetch();
  const route = conduxTunnelRoute({ dsn: DSN, fetch: doFetch });

  const response = await route(new Request("https://app.test/monitoring", { method: "POST", body: EVENT }));

  assert.equal(response.status, 200);
  assert.equal(calls.length, 1);
  assert.equal(
    calls[0].url,
    "https://ingest.condux.test/api/0198aaaa-0000-7000-8000-000000000001/store/",
  );
  // The route authenticates with its own key — never with anything the browser sent.
  assert.equal(calls[0].headers["x-condux-auth"], "pubkey123");
  assert.equal(calls[0].body, EVENT);
});

test("passes the relay's status through so the client transport can decide retries", async () => {
  const { doFetch } = recordingFetch(429, "");
  const route = conduxTunnelRoute({ dsn: DSN, fetch: doFetch });

  const response = await route(new Request("https://app.test/monitoring", { method: "POST", body: EVENT }));

  assert.equal(response.status, 429);
});

test("without a DSN configured it answers 503 and forwards nothing", async () => {
  // The route falls back to the env DSNs; clear them so this asserts the unconfigured path regardless
  // of the machine running the test.
  delete process.env.CONDUX_DSN;
  delete process.env.NEXT_PUBLIC_CONDUX_DSN;
  const { doFetch, calls } = recordingFetch();
  const route = conduxTunnelRoute({ fetch: doFetch });

  const response = await route(new Request("https://app.test/monitoring", { method: "POST", body: EVENT }));

  assert.equal(response.status, 503);
  assert.equal(calls.length, 0);
});

test("an oversized body is refused before it is forwarded", async () => {
  const { doFetch, calls } = recordingFetch();
  const route = conduxTunnelRoute({ dsn: DSN, fetch: doFetch });

  const response = await route(
    new Request("https://app.test/monitoring", { method: "POST", body: "x".repeat(1024 * 1024 + 1) }),
  );

  assert.equal(response.status, 413);
  assert.equal(calls.length, 0);
});
