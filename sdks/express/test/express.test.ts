import assert from "node:assert/strict";
import { test } from "node:test";
import { type FetchLike, conduxErrorHandler, init } from "../src/index.ts";

const DSN = "http://pub123@relay.test/7";

function recordingFetch() {
  const sent: string[] = [];
  const fetch: FetchLike = async (_url, init) => {
    sent.push(init.body);
    return { status: 202, headers: { get: () => null } };
  };
  return { fetch, last: () => JSON.parse(sent[sent.length - 1]) };
}

// Fire-and-forget capture: let the async send settle before reading what reached the relay.
const flush = () => new Promise((resolve) => setTimeout(resolve, 0));

test("conduxErrorHandler reports the error as unhandled and passes it on", async () => {
  const { fetch, last } = recordingFetch();
  init({ dsn: DSN, fetch });

  let forwarded: unknown;
  conduxErrorHandler()(new TypeError("route boom"), {}, {}, (err) => {
    forwarded = err;
  });
  await flush();

  const ex = last().exception.values[0];
  assert.equal(ex.type, "TypeError");
  assert.equal(ex.value, "route boom");
  assert.equal(ex.mechanism.handled, false);
  // The error is passed to the next handler, so the app still responds.
  assert.ok(forwarded instanceof TypeError);
});

test("conduxErrorHandler has arity 4 so Express treats it as error middleware", () => {
  assert.equal(conduxErrorHandler().length, 4);
});
