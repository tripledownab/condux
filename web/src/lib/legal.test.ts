import { afterEach, describe, expect, it, vi } from "vitest";
import { LegalDocument, legalUrl } from "./legal";

afterEach(() => {
  vi.unstubAllEnvs();
});

describe("legalUrl", () => {
  it("builds an absolute url for each document", () => {
    expect(legalUrl(LegalDocument.Terms, "https://condux.ai")).toBe("https://condux.ai/terms");
    expect(legalUrl(LegalDocument.Privacy, "https://condux.ai")).toBe("https://condux.ai/privacy");
  });

  it("tolerates a trailing slash on the configured base url", () => {
    expect(legalUrl(LegalDocument.Terms, "https://condux.ai/")).toBe("https://condux.ai/terms");
    expect(legalUrl(LegalDocument.Terms, "https://condux.ai///")).toBe("https://condux.ai/terms");
  });

  it("returns null when nothing is configured, rather than a same-origin path that 404s", () => {
    expect(legalUrl(LegalDocument.Terms, "")).toBeNull();
  });

  // The env is the production path: it decides whether a self-hosted install asks anyone to accept
  // terms that are not theirs. The injected-argument cases above never exercise it.
  it("reads the environment when no base url is passed", () => {
    vi.stubEnv("NEXT_PUBLIC_CONDUX_LEGAL_URL", "");
    expect(legalUrl(LegalDocument.Terms)).toBeNull();

    vi.stubEnv("NEXT_PUBLIC_CONDUX_LEGAL_URL", "https://condux.ai");
    expect(legalUrl(LegalDocument.Terms)).toBe("https://condux.ai/terms");
    expect(legalUrl(LegalDocument.Privacy)).toBe("https://condux.ai/privacy");
  });
});
