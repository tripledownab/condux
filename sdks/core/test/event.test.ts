import assert from "node:assert/strict";
import { test } from "node:test";
import { type FetchLike, Level, captureException, captureMessage, init } from "../src/index.ts";

const DSN = "http://pub123@relay.test/7";

// A fetch that records the last request body and reports success, so a test can inspect exactly
// what the SDK put on the wire. This is the coverage the transport-only suite lacked: it asserts
// the emitted JSON is the Sentry store shape the relay parses.
function recordingFetch() {
  const sent: { url: string; body: string }[] = [];
  const fetch: FetchLike = async (url, init) => {
    sent.push({ url, body: init.body });
    return { status: 202, headers: { get: () => null } };
  };
  return { fetch, last: () => JSON.parse(sent[sent.length - 1].body) };
}

test("captureException emits the Sentry store shape with a stack trace", async () => {
  const { fetch, last } = recordingFetch();
  init({ dsn: DSN, environment: "test", release: "app@1.2.3", fetch });

  function boom() {
    throw new TypeError("cannot read id of undefined");
  }
  try {
    boom();
  } catch (error) {
    await captureException(error);
  }

  const event = last();
  // Sentry field names, not the old native shape (no `exceptions`, `timestamp_unix_ms`, LEVEL_*).
  assert.equal(event.exceptions, undefined);
  assert.equal(event.timestamp_unix_ms, undefined);
  assert.equal(event.level, "error");
  assert.equal(event.platform, "javascript");
  assert.equal(event.environment, "test");
  assert.equal(event.release, "app@1.2.3");
  assert.equal(typeof event.timestamp, "number");
  assert.match(event.event_id, /^[0-9a-f]{32}$/);

  const ex = event.exception.values[0];
  assert.equal(ex.type, "TypeError");
  assert.equal(ex.value, "cannot read id of undefined");
  // Handled capture carries the Sentry mechanism (drives the unhandled badge).
  assert.equal(ex.mechanism.type, "generic");
  assert.equal(ex.mechanism.handled, true);

  // Frames are oldest-first, so the throwing function is last and marked in-app.
  const frames = ex.stacktrace.frames;
  assert.ok(frames.length > 0);
  const top = frames[frames.length - 1];
  assert.equal(top.function, "boom");
  assert.equal(top.in_app, true);
  assert.equal(typeof top.lineno, "number");
});

test("captureMessage emits a message event at the given level, no exception", async () => {
  const { fetch, last } = recordingFetch();
  init({ dsn: DSN, fetch });

  await captureMessage("checkout latency degraded", Level.Warning);

  const event = last();
  assert.equal(event.message, "checkout latency degraded");
  assert.equal(event.level, "warning");
  assert.equal(event.exception, undefined);
  // No environment/release configured, so they are omitted, not sent as null.
  assert.equal("environment" in event, false);
});

test("captureException can mark an exception unhandled (browser/edge global handlers do this)", async () => {
  const { fetch, last } = recordingFetch();
  init({ dsn: DSN, fetch });

  await captureException(new Error("uncaught"), false);

  const ex = last().exception.values[0];
  assert.equal(ex.mechanism.handled, false);
});

test("captureException reports non-Error throws without a stack", async () => {
  const { fetch, last } = recordingFetch();
  init({ dsn: DSN, fetch });

  await captureException("boom string");

  const ex = last().exception.values[0];
  assert.equal(ex.type, "Error");
  assert.equal(ex.value, "boom string");
  assert.equal(ex.stacktrace, undefined);
});
