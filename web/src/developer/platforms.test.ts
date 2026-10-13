import { describe, expect, it } from "vitest";
import messages from "@/messages/en.json";
import { PLATFORM_KEYS } from "@/src/projects/platforms";
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

  it("the Flutter build splits its debug info into the directory the CI step uploads from", () => {
    const flutter = PLATFORMS.find((platform) => platform.id === "flutter");
    const build =
      flutter?.snippets.find((snippet) => snippet.titleKey === "build")?.code(DSN) ?? "";
    const upload =
      flutter?.snippets.find((snippet) => snippet.titleKey === "symbols")?.code(DSN) ?? "";

    const directory = /--split-debug-info=(\S+)/.exec(build)?.[1];
    expect(directory).toBeTruthy();
    expect(upload).toContain(`for file in ${directory}/*.symbols`);
    expect(upload).toContain('-H "Authorization: Bearer $CONDUX_RELEASE_TOKEN"');
    expect(upload).toContain("/api/debug-files");
    // GitHub's own expression syntax, written out rather than interpolated by the template literal.
    // biome-ignore lint/suspicious/noTemplateCurlyInString: the literal GitHub Actions syntax is the point.
    expect(upload).toContain("${{ secrets.CONDUX_RELEASE_TOKEN }}");
  });

  it("every snippet title and every project platform has its label", () => {
    const titles = new Set(
      PLATFORMS.flatMap((platform) => platform.snippets.map((s) => s.titleKey)),
    );
    for (const title of titles) {
      expect(messages.developer.snippets).toHaveProperty(title);
    }
    for (const key of PLATFORM_KEYS) {
      expect(messages.platforms).toHaveProperty(key);
    }
  });

  it("a Flutter project can be created and opens the guide on its Flutter entry", () => {
    expect(PLATFORM_KEYS).toContain("flutter");
    expect(defaultPlatform("flutter").id).toBe("flutter");
  });

  it("defaultPlatform matches the project's platform, else falls back to the first", () => {
    expect(defaultPlatform("python").id).toBe("python");
    expect(defaultPlatform("Python").id).toBe("python");
    expect(defaultPlatform("cobol").id).toBe(PLATFORMS[0].id);
    expect(defaultPlatform(undefined).id).toBe(PLATFORMS[0].id);
  });
});
