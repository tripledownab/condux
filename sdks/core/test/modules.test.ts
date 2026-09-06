import assert from "node:assert/strict";
import { beforeEach, test } from "node:test";
import { recordingFetch } from "./helpers.ts";
// The caps are module-private, like scope.ts's MAX_BREADCRUMBS, so tests reach them through the module
// rather than widening the package's public surface for their own convenience.
import { MAX_MODULES, MODULES_INTERVAL_MS, modulesField } from "../src/modules.ts";
import { captureException, clearModules, init, setModules } from "../src/index.ts";

const DSN = "http://pub123@relay.test/7";

beforeEach(() => clearModules());

test("declared modules ride the wire under the Sentry top-level key", async () => {
  const { fetch, last } = recordingFetch();
  init({ dsn: DSN, fetch });
  setModules({ lodash: "4.17.11", express: "4.16.0" });

  await captureException(new Error("boom"));

  // The relay parses a top-level `modules` string map. A nested or renamed key would be dropped
  // silently by the parser, so the key and the shape are both asserted rather than a count.
  assert.deepEqual(last().modules, { express: "4.16.0", lodash: "4.17.11" });
});

/**
 * An event from a package that never declared an inventory must keep its exact previous shape. A
 * browser or edge build never declares one, so this is the shape of every event they send.
 */
test("an event carries no modules key at all when none were declared", async () => {
  const { fetch, last } = recordingFetch();
  init({ dsn: DSN, fetch });

  await captureException(new Error("boom"));

  assert.equal("modules" in last(), false);
});

/**
 * The behaviour that makes the feature affordable. Measured at about 11KB for a real application, so
 * attaching it to every event of a busy project is bytes spent for nothing: the server deduplicates
 * the inventory to one row per package per day. Remove the interval and this fails on the second event.
 */
test("the inventory rides the first event and then not again until the interval elapses", async () => {
  const { fetch, all } = recordingFetch();
  init({ dsn: DSN, fetch });
  setModules({ lodash: "4.17.11" });

  await captureException(new Error("one"));
  await captureException(new Error("two"));
  await captureException(new Error("three"));

  assert.equal(all().filter((event) => event.modules !== undefined).length, 1);
});

/**
 * Repeating matters as much as skipping. The event carrying the inventory can be dropped by a rate
 * limit or a quota rejection before anything parses it, so one attempt per process would lose that
 * day's inventory outright.
 */
test("the inventory rides again once the interval has elapsed", () => {
  setModules({ lodash: "4.17.11" });
  const start = 1_000_000;

  assert.notEqual(modulesField(start).modules, undefined);
  assert.equal(modulesField(start + MODULES_INTERVAL_MS - 1).modules, undefined);
  assert.notEqual(modulesField(start + MODULES_INTERVAL_MS).modules, undefined);
});

test("a fresh declaration is sent without waiting out the previous interval", () => {
  setModules({ lodash: "4.17.11" });
  const start = 1_000_000;
  assert.notEqual(modulesField(start).modules, undefined);

  setModules({ lodash: "4.17.21" });

  assert.deepEqual(modulesField(start + 1).modules, { lodash: "4.17.21" });
});

test("entries are sorted and capped, so every event carries the same set", () => {
  const many: Record<string, string> = {};
  for (let i = 0; i < MAX_MODULES + 50; i++) {
    // Zero padded so lexicographic order is also numeric order, making the survivors predictable.
    many[`pkg-${String(i).padStart(5, "0")}`] = "1.0.0";
  }
  setModules(many);

  const names = Object.keys(modulesField(1).modules ?? {});
  assert.equal(names.length, MAX_MODULES);
  assert.equal(names[0], "pkg-00000");
  assert.deepEqual(names, [...names].sort());
});

test("empty and malformed entries are dropped rather than reported as versions", () => {
  setModules({ good: "1.0.0", blank: "", "": "2.0.0" });

  assert.deepEqual(modulesField(1).modules, { good: "1.0.0" });
});

test("clearing removes the key entirely", () => {
  setModules({ lodash: "4.17.11" });
  setModules(null);

  assert.equal(modulesField(1).modules, undefined);
});
