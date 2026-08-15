/**
 * Per-event enrichment: the request an error happened during, and tags scoped to that one event.
 *
 * The point of this channel is that it is NOT the ambient scope. A server handles requests
 * concurrently in one process, so request detail set ambiently would cross between them, and these
 * tests pin the isolation directly rather than trusting the call site to be careful.
 */

import assert from "node:assert/strict";
import { beforeEach, test } from "node:test";
import { clearScope, captureException, captureMessage, init, setTag } from "../src/index.ts";
import { recordingFetch } from "./helpers.ts";

const DSN = "http://pub123@relay.test/7";

beforeEach(() => clearScope());

test("an event with no capture context has no request field at all", async () => {
  const { fetch, last } = recordingFetch();
  init({ dsn: DSN, fetch });

  await captureException(new Error("plain"));

  // Absence, not an empty object: an unenriched event must keep its exact previous wire shape, or
  // every existing golden expectation quietly changes meaning.
  assert.equal("request" in last(), false);
});

test("the request rides the wire in the Sentry store shape the relay parses", async () => {
  const { fetch, last } = recordingFetch();
  init({ dsn: DSN, fetch });

  await captureException(new Error("boom"), false, {
    request: { url: "/api/checkout", method: "POST", query_string: "step=2" },
  });

  assert.deepEqual(last().request, {
    url: "/api/checkout",
    method: "POST",
    query_string: "step=2",
  });
});

test("per-event tags merge over ambient tags instead of replacing them", async () => {
  const { fetch, last } = recordingFetch();
  init({ dsn: DSN, fetch });

  setTag("org", "acme");
  setTag("plan", "business");

  await captureException(new Error("boom"), false, { tags: { route: "/app/[id]", plan: "trial" } });

  // org survives (ambient), route is added (per event), plan is overridden by the per-event value.
  // A whole-object replace would drop org, which is the failure this asserts against.
  assert.deepEqual(last().tags, { org: "acme", plan: "trial", route: "/app/[id]" });
});

test("per-event tags do not leak into the next event", async () => {
  const { fetch, last } = recordingFetch();
  init({ dsn: DSN, fetch });
  setTag("org", "acme");

  await captureException(new Error("first"), false, { tags: { route: "/one" } });
  await captureException(new Error("second"));

  // The concurrency hazard in miniature: if this channel wrote through to the ambient scope, the
  // second event would still carry /one, which on a server would be another request's route.
  assert.deepEqual(last().tags, { org: "acme" });
});

test("captureMessage carries per-event context too", async () => {
  const { fetch, last } = recordingFetch();
  init({ dsn: DSN, fetch });

  await captureMessage("slow render", undefined, { request: { url: "/dashboard" } });

  assert.equal(last().request.url, "/dashboard");
});

test("the page URL is filled in from location when one exists", async () => {
  const { fetch, last } = recordingFetch();
  init({ dsn: DSN, fetch });

  // Node leaves location undefined, which is exactly why a server event stays untouched. Standing one
  // up here proves the browser path without needing a browser.
  const globals = globalThis as { location?: { href: string } };
  globals.location = { href: "https://app.test/checkout?step=2" };
  try {
    await captureException(new Error("client boom"), false);
    assert.equal(last().request.url, "https://app.test/checkout?step=2");
  } finally {
    globals.location = undefined;
  }
});

test("an explicit request URL wins over the ambient page URL", async () => {
  const { fetch, last } = recordingFetch();
  init({ dsn: DSN, fetch });

  const globals = globalThis as { location?: { href: string } };
  globals.location = { href: "https://app.test/page" };
  try {
    await captureException(new Error("boom"), false, { request: { url: "/api/explicit" } });
    assert.equal(last().request.url, "/api/explicit");
  } finally {
    globals.location = undefined;
  }
});
