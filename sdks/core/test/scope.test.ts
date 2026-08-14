import assert from "node:assert/strict";
import { beforeEach, test } from "node:test";
import { recordingFetch } from "./helpers.ts";
import {
  Level,
  addBreadcrumb,
  captureException,
  captureMessage,
  clearScope,
  init,
  setContext,
  setTag,
  setUser,
} from "../src/index.ts";

const DSN = "http://pub123@relay.test/7";


beforeEach(() => clearScope());

test("scope enrichment rides the wire in the exact Sentry store shape", async () => {
  const { fetch, last } = recordingFetch();
  init({ dsn: DSN, fetch });

  setUser({ id: "u-9", email: "person@example.test" });
  setTag("org", "acme");
  setTag("plan", "business");
  setContext("subscription", { seats: 12, trial: false });
  addBreadcrumb({ message: "opened checkout", category: "navigation" });
  addBreadcrumb({ message: "clicked pay", category: "ui.click", level: Level.Info });
  await captureMessage("boom");

  const event = last();
  // Field-by-field: these are the names the relay's parser reads (user/tags/contexts, and
  // breadcrumbs in the {values: []} envelope) — a rename here silently drops the data server-side.
  assert.deepEqual(event.user, { id: "u-9", email: "person@example.test" });
  assert.deepEqual(event.tags, { org: "acme", plan: "business" });
  assert.deepEqual(event.contexts, { subscription: { seats: 12, trial: false } });
  assert.equal(event.breadcrumbs.values.length, 2);
  assert.equal(event.breadcrumbs.values[0].message, "opened checkout");
  assert.equal(event.breadcrumbs.values[1].category, "ui.click");
  assert.equal(typeof event.breadcrumbs.values[0].timestamp, "number");
});

test("captureException carries the scope too (shared dispatch, both entry points)", async () => {
  const { fetch, last } = recordingFetch();
  init({ dsn: DSN, fetch });

  setUser({ id: "u-2" });
  addBreadcrumb({ message: "before the crash" });
  await captureException(new Error("boom"));

  const event = last();
  assert.deepEqual(event.user, { id: "u-2" });
  assert.equal(event.breadcrumbs.values[0].message, "before the crash");
  assert.equal(event.exception.values[0].value, "boom");
});

test("an unenriched event keeps its exact wire shape (no empty keys)", async () => {
  const { fetch, last } = recordingFetch();
  init({ dsn: DSN, fetch });

  await captureMessage("plain");

  const event = last();
  assert.equal("user" in event, false);
  assert.equal("tags" in event, false);
  assert.equal("contexts" in event, false);
  assert.equal("breadcrumbs" in event, false);
});

test("null clears: user, a single tag, a single context", async () => {
  const { fetch, last } = recordingFetch();
  init({ dsn: DSN, fetch });

  setUser({ id: "u-1" });
  setTag("keep", "yes");
  setTag("drop", "temp");
  setContext("gone", { a: 1 });
  setUser(null);
  setTag("drop", null);
  setContext("gone", null);
  await captureMessage("after clears");

  const event = last();
  assert.equal("user" in event, false);
  assert.deepEqual(event.tags, { keep: "yes" });
  assert.equal("contexts" in event, false);
});

test("the breadcrumb trail is capped, keeping the newest", async () => {
  const { fetch, last } = recordingFetch();
  init({ dsn: DSN, fetch });

  for (let i = 1; i <= 40; i++) {
    addBreadcrumb({ message: `step ${i}` });
  }
  await captureMessage("capped");

  const values = last().breadcrumbs.values;
  assert.equal(values.length, 30);
  assert.equal(values[0].message, "step 11"); // oldest surviving
  assert.equal(values[29].message, "step 40"); // newest last
});
