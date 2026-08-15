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

test("the request and route Next supplies reach the event", async () => {
  const { fetch, last } = recordingFetch();
  register({ dsn: DSN, fetch });

  // Next's real shapes, per its onRequestError types. `path` carries the query string inline.
  await captureRequestError(
    new Error("checkout failed"),
    { path: "/checkout?step=2", method: "POST", headers: { cookie: "session=secret" } },
    { routerKind: "App Router", routePath: "/app/checkout/[id]", routeType: "route" },
  );

  const event = last();
  // Split, because the wire shape keeps the query apart from the URL.
  assert.deepEqual(event.request, { url: "/checkout", method: "POST", query_string: "step=2" });
  // routePath is the parameterised form, so every dynamic instance groups under one tag value.
  assert.deepEqual(event.tags, {
    route: "/app/checkout/[id]",
    route_type: "route",
    router: "App Router",
  });
});

test("request headers are never forwarded, even though Next supplies them", async () => {
  const { fetch, last } = recordingFetch();
  register({ dsn: DSN, fetch });

  await captureRequestError(
    new Error("boom"),
    { path: "/x", method: "GET", headers: { cookie: "session=secret", authorization: "Bearer t" } },
    undefined,
  );

  // The relay scrubs sensitive header keys, but the stronger guarantee is not sending them at all.
  // Asserted on the serialized body so a nested copy anywhere in the event would still fail this.
  assert.equal("headers" in last().request, false);
  assert.equal(JSON.stringify(last()).includes("secret"), false);
  assert.equal(JSON.stringify(last()).includes("Bearer"), false);
});

test("a request with no path or method adds no request field", async () => {
  const { fetch, last } = recordingFetch();
  register({ dsn: DSN, fetch });

  await captureRequestError(new Error("boom"));

  // Next may call the hook with nothing useful; the event must keep its plain shape rather than
  // carrying an empty object the relay then has to interpret.
  assert.equal("request" in last(), false);
  assert.equal("tags" in last(), false);
});

test("initClient initializes reporting from the client DSN", async () => {
  const { fetch, last } = recordingFetch();
  initClient({ dsn: DSN, fetch });

  await captureMessage("client ready", Level.Info);

  assert.equal(last().message, "client ready");
  assert.equal(last().level, "info");
});
