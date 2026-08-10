import { describe, expect, it } from "vitest";
import { prettyJson } from "./json";

describe("prettyJson", () => {
  it("pretty-prints valid JSON", () => {
    expect(prettyJson('{"a":1}')).toBe('{\n  "a": 1\n}');
  });

  it("returns the raw string rather than hiding non-JSON input", () => {
    expect(prettyJson("not json")).toBe("not json");
  });
});
