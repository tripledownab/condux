import { describe, expect, it } from "vitest";
import { truncate } from "./strings";

describe("truncate", () => {
  it("leaves a short value unchanged", () => {
    expect(truncate("hello", 32)).toBe("hello");
    expect(truncate("x".repeat(32), 32)).toBe("x".repeat(32));
  });

  it("cuts a long value to max chars and appends an ellipsis", () => {
    expect(truncate("x".repeat(40), 32)).toBe(`${"x".repeat(32)}…`);
  });
});
