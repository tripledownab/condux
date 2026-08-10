import { describe, expect, it } from "vitest";
import { slugify, truncate } from "./strings";

describe("truncate", () => {
  it("leaves a short value unchanged", () => {
    expect(truncate("hello", 32)).toBe("hello");
    expect(truncate("x".repeat(32), 32)).toBe("x".repeat(32));
  });

  it("cuts a long value to max chars and appends an ellipsis", () => {
    expect(truncate("x".repeat(40), 32)).toBe(`${"x".repeat(32)}…`);
  });
});

describe("slugify", () => {
  it("lowercases and dashes non-alphanumeric runs", () => {
    expect(slugify("My Web App")).toBe("my-web-app");
    expect(slugify("Payments API v2!")).toBe("payments-api-v2");
  });

  it("trims leading and trailing dashes", () => {
    expect(slugify("  spaced out  ")).toBe("spaced-out");
    expect(slugify("--edge--")).toBe("edge");
  });
});
