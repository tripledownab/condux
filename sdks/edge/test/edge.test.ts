import assert from "node:assert/strict";
import { test } from "node:test";
import { type FetchLike, init, wrapFetch } from "../src/index.ts";

const DSN = "http://pub123@relay.test/7";

// Records the last event body so a test can assert exactly what wrapFetch reported to the relay.
function recordingFetch() {
  const sent: string[] = [];
  const fetch: FetchLike = async (_url, init) => {
    sent.push(init.body);
    return { status: 202, headers: { get: () => null } };
  };
  return { fetch, last: () => JSON.parse(sent[sent.length - 1]) };
}

test("wrapFetch reports a thrown error as unhandled and rethrows it", async () => {
  const { fetch, last } = recordingFetch();
  init({ dsn: DSN, fetch });

  const wrapped = wrapFetch(async () => {
    throw new Error("edge boom");
  });

  await assert.rejects(() => wrapped(new Request("http://worker.test/")), /edge boom/);

  const ex = last().exception.values[0];
  assert.equal(ex.value, "edge boom");
  assert.equal(ex.mechanism.handled, false);
});

test("wrapFetch passes a successful response through and reports nothing", async () => {
  let reported = false;
  const fetch: FetchLike = async () => {
    reported = true;
    return { status: 202, headers: { get: () => null } };
  };
  init({ dsn: DSN, fetch });

  const wrapped = wrapFetch(async () => new Response("ok", { status: 200 }));
  const response = await wrapped(new Request("http://worker.test/"));

  assert.equal(response.status, 200);
  assert.equal(reported, false);
});
