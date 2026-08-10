import { describe, expect, it } from "vitest";
import { defaultPlatform, PLATFORMS } from "./platforms";

const DSN = "http://pub123@localhost:9010/proj-uuid";

describe("platforms catalog", () => {
  it("has unique ids", () => {
    const ids = PLATFORMS.map((platform) => platform.id);
    expect(new Set(ids).size).toBe(ids.length);
  });

  it("every platform ships an install and an initialize snippet", () => {
    for (const platform of PLATFORMS) {
      const titles = platform.snippets.map((snippet) => snippet.titleKey);
      expect(titles).toContain("install");
      expect(titles).toContain("initialize");
    }
  });

  it("the initialize snippet is prefilled with the DSN", () => {
    for (const platform of PLATFORMS) {
      const initialize = platform.snippets.find((snippet) => snippet.titleKey === "initialize");
      expect(initialize?.code(DSN)).toContain(DSN);
    }
  });

  it("every framework snippet names its adapter", () => {
    for (const platform of PLATFORMS) {
      for (const snippet of platform.snippets) {
        if (snippet.titleKey === "framework") {
          expect(snippet.framework).toBeTruthy();
        }
      }
    }
  });

  it("the Next.js catalog includes a source-map upload step (ADR-0028)", () => {
    const nextjs = PLATFORMS.find((platform) => platform.id === "nextjs");
    const sourcemaps = nextjs?.snippets.find((snippet) => snippet.titleKey === "sourcemaps");
    expect(sourcemaps?.code(DSN)).toContain("condux-sourcemaps");
  });

  it("defaultPlatform matches the project's platform, else falls back to the first", () => {
    expect(defaultPlatform("python").id).toBe("python");
    expect(defaultPlatform("Python").id).toBe("python");
    expect(defaultPlatform("cobol").id).toBe(PLATFORMS[0].id);
    expect(defaultPlatform(undefined).id).toBe(PLATFORMS[0].id);
  });
});
