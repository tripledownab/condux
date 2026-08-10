import { describe, expect, it } from "vitest";
import { parseEventPayload, primaryException } from "./event-payload";
import { buildFacets, usersAffected } from "./issue-facets";

// A stored payload as the backend writes it: the normalized Event serialized with PascalCase keys.
const payload = JSON.stringify({
  EventId: "abc",
  Level: 4,
  Release: "1.4.0",
  Dist: "build-42",
  Environment: "production",
  Transaction: "POST /checkout",
  TraceId: "0af7651916cd43dd8448eb211c80319c",
  SdkName: "sentry.javascript.browser",
  SdkVersion: "8.0.0",
  UserKey: "k1",
  User: { Id: "user-7", Username: "wally", Email: "[redacted]", IpAddress: null },
  Request: { Url: "https://shop.acme.io/checkout", Method: "POST" },
  Contexts: { browser: "Chrome 126.0", os: "iOS 18.1" },
  Tags: { region: "eu" },
  Modules: { stripe: "14.1.0" },
  Exceptions: [
    {
      Type: "TypeError",
      Value: "boom",
      Handled: false,
      Stacktrace: {
        Frames: [
          { Filename: "node_modules/lib.js", Function: "outer", Lineno: 1, InApp: false },
          {
            Filename: "src/cart.js",
            Function: "total",
            Lineno: 5,
            InApp: true,
            ContextLine: "  total()",
            ContextBefore: ["function total() {", "  validate()"],
            ContextAfter: ["}"],
            Vars: { cart: "None" },
          },
        ],
      },
    },
  ],
  Breadcrumbs: [
    {
      TimestampUnixMs: 1789199990000,
      Category: "fetch",
      Message: "POST /api/pay",
      Level: 2,
      Data: { status_code: "502" },
    },
  ],
});

describe("parseEventPayload", () => {
  it("parses the PascalCase stored payload field by field", () => {
    const event = parseEventPayload(payload);
    if (event === null) {
      throw new Error("payload should parse");
    }

    const primary = primaryException(event);
    expect(primary?.type).toBe("TypeError");
    expect(primary?.handled).toBe(false); // the unhandled badge source
    expect(primary?.frames[1]).toMatchObject({
      filename: "src/cart.js",
      function: "total",
      lineno: 5,
      inApp: true,
      contextLine: "  total()",
      contextBefore: ["function total() {", "  validate()"],
      contextAfter: ["}"],
      vars: { cart: "None" },
    });

    expect(event.breadcrumbs[0]).toMatchObject({
      category: "fetch",
      message: "POST /api/pay",
      data: { status_code: "502" },
    });
    expect(event.contexts.browser).toBe("Chrome 126.0");
    expect(event.tags.region).toBe("eu");
    expect(event.user).toMatchObject({ id: "user-7", username: "wally" });
    expect(event.userKey).toBe("k1");
    expect(event.request).toMatchObject({ url: "https://shop.acme.io/checkout", method: "POST" });
    expect(event.release).toBe("1.4.0");
    expect(event.traceId).toBe("0af7651916cd43dd8448eb211c80319c");
  });

  it("returns null for unparsable payloads and empties for absent fields", () => {
    expect(parseEventPayload("not json")).toBeNull();

    const minimal = parseEventPayload("{}");
    expect(minimal).not.toBeNull();
    expect(minimal?.exceptions).toEqual([]);
    expect(minimal?.userKey).toBe("");
    expect(minimal?.user).toBeUndefined();
  });
});

describe("facets", () => {
  const events = [
    parseEventPayload(payload),
    parseEventPayload(payload.replace('"k1"', '"k2"').replace("Chrome 126.0", "Firefox 128")),
    parseEventPayload(payload), // same user as the first
  ].filter((event) => event !== null);

  it("counts distinct users via the pseudonymous key", () => {
    expect(usersAffected(events)).toBe(2);
  });

  it("ranks top values per dimension with shares from the sample", () => {
    const facets = buildFacets(events);
    const browser = facets.find((facet) => facet.key === "browser");
    expect(browser?.values[0]).toEqual({ value: "Chrome 126.0", share: 2 / 3 });
    expect(browser?.values[1]).toEqual({ value: "Firefox 128", share: 1 / 3 });
    // Tags participate as dimensions beside contexts.
    expect(facets.some((facet) => facet.key === "region")).toBe(true);
  });
});
