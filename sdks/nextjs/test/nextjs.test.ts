import assert from "node:assert/strict";
import { test } from "node:test";
import { type FetchLike, Level, captureMessage, captureRequestError, initClient, register } from "../src/index.ts";

const DSN = "http://pub123@relay.test/7";

// Records the last event body so a test can assert exactly what reached the relay.
function recordingFetch() {
  const sent: string[] = [];
  const fetch: FetchLike = async (_url, init) => {
    sent.push(init.body);
    return { status: 202, headers: { get: () => null } };
  };
  return { fetch, last: () => JSON.parse(sent[sent.length - 1]) };
}

test("is inert without a DSN, and captureRequestError never throws unconfigured", async () => {
  assert.doesNotThrow(() => register({}));
  assert.doesNotThrow(() => initClient({}));
  // Next fires onRequestError even before register succeeds — it must be a safe no-op.
  await assert.doesNotReject(() => captureRequestError(new Error("before register")));
});

test("register + captureRequestError report a server error as unhandled", async () => {
  const { fetch, last } = recordingFetch();
  register({ dsn: DSN, environment: "production", release: "web@1.2.3", fetch });

  // Next's onRequestError signature: (error, request, context).
  await captureRequestError(new Error("server boom"), { path: "/api/x" }, { routerKind: "App Router" });

  const event = last();
  assert.equal(event.environment, "production");
  assert.equal(event.release, "web@1.2.3");
  const ex = event.exception.values[0];
  assert.equal(ex.value, "server boom");
  assert.equal(ex.mechanism.handled, false);
});

test("initClient initializes reporting from the client DSN", async () => {
  const { fetch, last } = recordingFetch();
  initClient({ dsn: DSN, fetch });

  await captureMessage("client ready", Level.Info);

  assert.equal(last().message, "client ready");
  assert.equal(last().level, "info");
});
