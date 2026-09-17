import assert from "node:assert/strict";
import { test } from "node:test";
import { conduxTunnelRoute } from "../src/index.ts";

const DSN = "https://pubkey123@ingest.condux.test/0198aaaa-0000-7000-8000-000000000001";
const EVENT = JSON.stringify({ event_id: "e".repeat(32), level: "error" });
const LIMIT = 1024 * 1024;

function recordingFetch(status = 200, body = '{"id":"e"}') {
  const calls: { url: string; headers: Record<string, string>; body: string }[] = [];
  const doFetch = (async (url: RequestInfo | URL, init?: RequestInit) => {
    calls.push({
      url: String(url),
      headers: (init?.headers ?? {}) as Record<string, string>,
      // The route forwards bytes rather than a string, so read them back the way the relay would.
      body: new TextDecoder().decode(init?.body as ArrayBuffer),
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
    new Request("https://app.test/monitoring", { method: "POST", body: "x".repeat(LIMIT + 1) }),
  );

  assert.equal(response.status, 413);
  assert.equal(calls.length, 0);
});

// The route is public and unauthenticated, so this is the test that decides whether the cap protects
// anything: a refusal issued after the whole stream is drained costs exactly what not checking costs.
// A chunked body never has to end, so an unbounded read here is an out-of-memory kill of the dashboard
// process, not a slow request. The stream counts what it was asked for, which is the only way to see
// the difference: the status is 413 either way.
test("stops pulling a never-ending body instead of draining it first", async () => {
  const chunk = new Uint8Array(64 * 1024);
  const ceiling = LIMIT * 8;
  let produced = 0;
  // Stops at a ceiling well past the cap rather than running for ever, so a regression fails this test
  // with a byte count instead of hanging the suite until CI times out.
  const endless = new ReadableStream<Uint8Array>({
    pull(controller) {
      if (produced >= ceiling) {
        controller.close();
        return;
      }
      produced += chunk.byteLength;
      controller.enqueue(chunk);
    },
  });
  const { doFetch, calls } = recordingFetch();
  const route = conduxTunnelRoute({ dsn: DSN, fetch: doFetch });

  const response = await route(
    new Request("https://app.test/monitoring", {
      method: "POST",
      body: endless,
      // @ts-expect-error duplex is required for a streaming body and is not in the lib.dom typings.
      duplex: "half",
    }),
  );

  assert.equal(response.status, 413);
  assert.equal(calls.length, 0);
  // Allow the stream's own read-ahead, but nothing like "until the attacker stops sending".
  assert.ok(produced <= LIMIT * 2, `pulled ${produced} bytes for a ${LIMIT}-byte cap`);
});

// A byte limit compared against a string length is not a byte limit. Each of these characters is one
// UTF-16 code unit and three UTF-8 bytes, so this body is well under the cap by the measure the old
// check used and well over it by the measure that decides how much memory the process holds.
test("measures the cap in bytes rather than in characters", async () => {
  const body = "あ".repeat(400_000); // 400k UTF-16 units, 1.2MB of UTF-8
  assert.ok(body.length < LIMIT);
  assert.ok(new TextEncoder().encode(body).byteLength > LIMIT);
  const { doFetch, calls } = recordingFetch();
  const route = conduxTunnelRoute({ dsn: DSN, fetch: doFetch });

  const response = await route(new Request("https://app.test/monitoring", { method: "POST", body }));

  assert.equal(response.status, 413);
  assert.equal(calls.length, 0);
});

test("a body within the cap still goes through unchanged", async () => {
  const body = "あ".repeat(300_000); // 900KB of UTF-8, under the cap
  const { doFetch, calls } = recordingFetch();
  const route = conduxTunnelRoute({ dsn: DSN, fetch: doFetch });

  const response = await route(new Request("https://app.test/monitoring", { method: "POST", body }));

  assert.equal(response.status, 200);
  assert.equal(calls[0].body, body);
});
