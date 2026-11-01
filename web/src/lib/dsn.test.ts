import { describe, expect, it } from "vitest";
import { buildDsn, toOtlpTarget } from "./dsn";

describe("toOtlpTarget", () => {
  it("points the exporter at the project's OTLP base and keeps the key for the header", () => {
    expect(toOtlpTarget("https://deadbeef@ingest.condux.ai/0190a3e2-7f1c-uuid")).toEqual({
      endpoint: "https://ingest.condux.ai/api/0190a3e2-7f1c-uuid",
      key: "deadbeef",
    });
    expect(toOtlpTarget("http://abc123@localhost:9010/0190a3e2-7f1c-uuid")?.endpoint).toBe(
      "http://localhost:9010/api/0190a3e2-7f1c-uuid",
    );
  });

  it("answers null for a value that is not a URL rather than throwing", () => {
    expect(toOtlpTarget("<your-dsn>")).toBeNull();
  });
});

describe("buildDsn", () => {
  it("builds scheme://publicKey@host/<project-uuid> from the ingest url", () => {
    expect(buildDsn("abc123", "0190a3e2-7f1c-uuid", "http://localhost:9010")).toBe(
      "http://abc123@localhost:9010/0190a3e2-7f1c-uuid",
    );
    expect(buildDsn("deadbeef", "0190a3e2-7f1c-uuid", "https://ingest.condux.ai")).toBe(
      "https://deadbeef@ingest.condux.ai/0190a3e2-7f1c-uuid",
    );
  });
});
