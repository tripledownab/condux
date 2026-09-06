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

test("the request Express holds reaches the event, mounted path included", async () => {
  const { fetch, last } = recordingFetch();
  init({ dsn: DSN, fetch });

  // A router mounted at /api rewrites `url` to the path within the router, so only originalUrl still
  // names the endpoint the client called. Reporting `url` here would say "/sync" for "/api/sync?since=1".
  conduxErrorHandler()(
    new Error("boom"),
    {
      originalUrl: "/api/sync?since=1",
      url: "/sync?since=1",
      method: "POST",
      headers: { cookie: "session=supersecret", authorization: "Bearer tok" },
    },
    {},
    () => {},
  );
  await flush();

  const event = last();
  assert.deepEqual(event.request, { url: "/api/sync", method: "POST", query_string: "since=1" });
  // The relay scrubs sensitive header keys, but not sending them at all is the stronger guarantee.
  assert.equal(JSON.stringify(event).includes("supersecret"), false);
  assert.equal(JSON.stringify(event).includes("Bearer"), false);
});

test("a request Express cannot describe adds no request field", async () => {
  const { fetch, last } = recordingFetch();
  init({ dsn: DSN, fetch });

  conduxErrorHandler()(new Error("boom"), {}, {}, () => {});
  await flush();

  // Absence, not an empty object: an event with nothing to say about the request keeps its plain shape.
  assert.equal("request" in last(), false);
});

// --- Caller-caused errors (ADR-0044) -----------------------------------------------------------
//
// These assert an ABSENCE, so each one first proves the recorder would have seen a report, by sending a
// plain error through the same handler and finding it. Without that, a broken recorder passes them all.

/** What body-parser actually attaches, measured against a real Express app rather than guessed. */
function bodyParserError(name: string, status: number, type: string) {
  const error = new Error(`${type} failed`);
  error.name = name;
  return Object.assign(error, { status, statusCode: status, expose: true, type });
}

test("a malformed JSON body is passed on but not filed as an application defect", async () => {
  const { fetch, last } = recordingFetch();
  init({ dsn: DSN, fetch });

  // Control: the recorder is working and this handler does report.
  conduxErrorHandler()(new Error("a real fault"), {}, {}, () => {});
  await flush();
  assert.equal(last().exception.values[0].value, "a real fault");

  let forwarded: unknown;
  conduxErrorHandler()(bodyParserError("SyntaxError", 400, "entity.parse.failed"), {}, {}, (err) => {
    forwarded = err;
  });
  await flush();

  // Still the control event: nothing new reached the relay.
  assert.equal(last().exception.values[0].value, "a real fault");
  // And the app's own error handling still runs, so its 400 response is unchanged.
  assert.equal((forwarded as { status: number }).status, 400);
});

test("a body over the parser's limit is not filed either", async () => {
  const { fetch, last } = recordingFetch();
  init({ dsn: DSN, fetch });

  conduxErrorHandler()(new Error("a real fault"), {}, {}, () => {});
  await flush();

  conduxErrorHandler()(bodyParserError("PayloadTooLargeError", 413, "entity.too.large"), {}, {}, () => {});
  await flush();

  assert.equal(last().exception.values[0].value, "a real fault");
});

test("a 5xx carrying a status is still the application's defect", async () => {
  const { fetch, last } = recordingFetch();
  init({ dsn: DSN, fetch });

  // The rule is a 4xx RANGE, not "has a status". A 503 from the app is the app's problem, and widening
  // this to any status would silently stop reporting them.
  conduxErrorHandler()(bodyParserError("ServiceUnavailableError", 503, "upstream.down"), {}, {}, () => {});
  await flush();

  assert.equal(last().exception.values[0].type, "ServiceUnavailableError");
  assert.equal(last().exception.values[0].mechanism.handled, false);
});

test("statusCode alone is enough, since not every thrower sets both", async () => {
  const { fetch, last } = recordingFetch();
  init({ dsn: DSN, fetch });

  conduxErrorHandler()(new Error("a real fault"), {}, {}, () => {});
  await flush();

  const error = Object.assign(new Error("unsupported media type"), { statusCode: 415 });
  conduxErrorHandler()(error, {}, {}, () => {});
  await flush();

  assert.equal(last().exception.values[0].value, "a real fault");
});
