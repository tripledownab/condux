import { describe, expect, it } from "vitest";
import { frameGithubUrl } from "./github-links";

const repo = {
  id: "r1",
  projectId: 7,
  repoFullName: "acme/backend",
  defaultBranch: "main",
  createdAt: "",
};
const mapping = { id: "m1", repoLinkId: "r1", stackRoot: "src/", sourceRoot: "services/api/src/" };

function frame(filename: string, lineno = 42) {
  return {
    filename,
    lineno,
    inApp: true,
    inlined: false,
    contextBefore: [],
    contextAfter: [],
    vars: {},
  };
}

describe("frameGithubUrl", () => {
  it("maps a matching frame onto the repo's default branch with a line anchor", () => {
    expect(frameGithubUrl(frame("src/checkout/cart.ts"), [repo], [mapping])).toBe(
      "https://github.com/acme/backend/blob/main/services/api/src/checkout/cart.ts#L42",
    );
  });

  it("ignores frames outside every mapping and unmapped repos", () => {
    expect(frameGithubUrl(frame("node_modules/lib.js"), [repo], [mapping])).toBeNull();
    expect(frameGithubUrl(frame("src/a.ts"), [], [mapping])).toBeNull();
    expect(
      frameGithubUrl({ ...frame("src/a.ts"), filename: undefined }, [repo], [mapping]),
    ).toBeNull();
  });

  it("takes the longest matching stack root, whatever the list order", () => {
    // The mappings arrive sorted by stack root, so an empty root for the whole source tree comes first.
    const tree = { id: "m2", repoLinkId: "r1", stackRoot: "", sourceRoot: "src/main/java/" };
    const billing = {
      id: "m3",
      repoLinkId: "r1",
      stackRoot: "com/acme/billing/",
      sourceRoot: "billing/src/main/java/com/acme/billing/",
    };
    expect(frameGithubUrl(frame("com/acme/billing/Invoice.java"), [repo], [tree, billing])).toBe(
      "https://github.com/acme/backend/blob/main/billing/src/main/java/com/acme/billing/Invoice.java#L42",
    );
    expect(frameGithubUrl(frame("com/acme/web/Home.java"), [repo], [tree, billing])).toBe(
      "https://github.com/acme/backend/blob/main/src/main/java/com/acme/web/Home.java#L42",
    );
  });

  it("normalizes leading ./ and skips the anchor without a line number", () => {
    expect(frameGithubUrl(frame("./src/a.ts", 0), [repo], [mapping])).toBe(
      "https://github.com/acme/backend/blob/main/services/api/src/a.ts",
    );
  });
});
