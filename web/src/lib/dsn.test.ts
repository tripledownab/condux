import { describe, expect, it } from "vitest";
import { buildDsn } from "./dsn";

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
