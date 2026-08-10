import assert from "node:assert/strict";
import { test } from "node:test";
import { ConduxErrorBoundary, type FetchLike, init } from "../src/index.ts";

const DSN = "http://pub123@relay.test/7";

function recordingFetch() {
  const sent: string[] = [];
  const fetch: FetchLike = async (_url, init) => {
    sent.push(init.body);
    return { status: 202, headers: { get: () => null } };
  };
  return { fetch, last: () => JSON.parse(sent[sent.length - 1]) };
}

// captureException is fire-and-forget; let the async send settle before reading what reached the relay.
const flush = () => new Promise((resolve) => setTimeout(resolve, 0));

test("componentDidCatch reports a render error as unhandled", async () => {
  const { fetch, last } = recordingFetch();
  init({ dsn: DSN, fetch });

  // Construct + drive the lifecycle method directly (no DOM render needed to exercise the reporting).
  const boundary = new ConduxErrorBoundary({});
  boundary.componentDidCatch(new TypeError("render boom"), { componentStack: "\n  at App" });
  await flush();

  const ex = last().exception.values[0];
  assert.equal(ex.type, "TypeError");
  assert.equal(ex.value, "render boom");
  assert.equal(ex.mechanism.handled, false);
});

test("getDerivedStateFromError flips the boundary into its error state", () => {
  assert.deepEqual(ConduxErrorBoundary.getDerivedStateFromError(), { hasError: true });
});
