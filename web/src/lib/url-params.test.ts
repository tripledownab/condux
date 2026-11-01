import { afterEach, describe, expect, it } from "vitest";
import { readSearchParam, removeSearchParams } from "./url-params";

describe("url params", () => {
  afterEach(() => window.history.replaceState(null, "", "/"));

  it("reads a parameter and drops only the ones named, keeping the rest of the URL", () => {
    window.history.replaceState(null, "", "/projects/abc?tab=linear&linear=connected&keep=1#top");

    expect(readSearchParam("linear")).toBe("connected");
    expect(readSearchParam("missing")).toBeNull();

    removeSearchParams("linear", "selection");

    expect(window.location.pathname).toBe("/projects/abc");
    expect(window.location.search).toBe("?tab=linear&keep=1");
    expect(window.location.hash).toBe("#top");
  });
});
