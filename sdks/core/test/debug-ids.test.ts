import assert from "node:assert/strict";
import { afterEach, test } from "node:test";
import { type FetchLike, captureException, init } from "../src/index.ts";

const DSN = "http://pub123@relay.test/7";
const CHUNK_URL = "https://app.test/_next/static/chunks/checkout-4f2a1c.js";
const DEBUG_ID = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

// The registry exactly as the withConduxConfig snippet writes it: the chunk's debugId keyed by a stack
// captured inside the chunk, whose innermost frame is the chunk's own URL.
const REGISTRY_STACK = `Error\n    at ${CHUNK_URL}:1:120`;

function recordingFetch() {
  const sent: string[] = [];
  const fetch: FetchLike = async (_url, init) => {
    sent.push(init.body);
    return { status: 200, headers: { get: () => null } };
  };
  return { fetch, last: () => JSON.parse(sent[sent.length - 1]) };
}

// An error whose stack points into the registered chunk, the way a real minified-bundle error does.
function chunkError(): Error {
  const error = new Error("cannot read total of undefined");
  error.stack = `Error: cannot read total of undefined\n    at t (${CHUNK_URL}:1:48213)`;
  return error;
}

afterEach(() => {
  delete (globalThis as Record<string, unknown>)._conduxDebugIds;
});

test("an event from a registered chunk carries the debug_meta image the symbolicator keys on", async () => {
  (globalThis as Record<string, unknown>)._conduxDebugIds = { [REGISTRY_STACK]: DEBUG_ID };
  const { fetch, last } = recordingFetch();
  init({ dsn: DSN, fetch });

  await captureException(chunkError());

  const event = last();
  // code_file must equal the frame's abs_path exactly — that string equality is the server-side match.
  assert.deepEqual(event.debug_meta, {
    images: [{ type: "sourcemap", code_file: CHUNK_URL, debug_id: DEBUG_ID }],
  });
  const frame = event.exception.values[0].stacktrace.frames[0];
  assert.equal(frame.abs_path, CHUNK_URL);
});

test("without a registry the event keeps its exact wire shape (no debug_meta key)", async () => {
  const { fetch, last } = recordingFetch();
  init({ dsn: DSN, fetch });

  await captureException(chunkError());

  assert.equal("debug_meta" in last(), false);
});

test("frames from unregistered files get no image", async () => {
  (globalThis as Record<string, unknown>)._conduxDebugIds = { [REGISTRY_STACK]: DEBUG_ID };
  const { fetch, last } = recordingFetch();
  init({ dsn: DSN, fetch });

  const error = new Error("elsewhere");
  error.stack = "Error: elsewhere\n    at t (https://app.test/other-chunk.js:1:10)";
  await captureException(error);

  assert.equal("debug_meta" in last(), false);
});

test("a malformed registry never breaks capture", async () => {
  (globalThis as Record<string, unknown>)._conduxDebugIds = { "not a stack": 42, "": null };
  const { fetch, last } = recordingFetch();
  init({ dsn: DSN, fetch });

  const result = await captureException(chunkError());

  assert.equal(result.ok, true);
  assert.equal("debug_meta" in last(), false);
});
