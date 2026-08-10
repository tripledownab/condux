import { describe, expect, it } from "vitest";
import { formatRelativeTime, formatTimestamp } from "./time";

describe("formatRelativeTime", () => {
  const now = new Date("2026-07-17T12:00:00Z");

  it("renders seconds, minutes, hours and days", () => {
    expect(formatRelativeTime("2026-07-17T11:59:30Z", now)).toBe("30s");
    expect(formatRelativeTime("2026-07-17T11:45:00Z", now)).toBe("15m");
    expect(formatRelativeTime("2026-07-17T09:00:00Z", now)).toBe("3h");
    expect(formatRelativeTime("2026-07-15T12:00:00Z", now)).toBe("2d");
  });

  it("falls back to the ISO date past a week", () => {
    expect(formatRelativeTime("2026-07-01T12:00:00Z", now)).toBe("2026-07-01");
  });

  it("clamps a future timestamp to 0s rather than going negative", () => {
    expect(formatRelativeTime("2026-07-17T12:00:30Z", now)).toBe("0s");
  });
});

describe("formatTimestamp", () => {
  it("trims an ISO timestamp to date and time", () => {
    expect(formatTimestamp("2026-07-17T11:59:00.123Z")).toBe("2026-07-17 11:59:00");
  });

  it("returns the input unchanged when it is not an ISO timestamp", () => {
    expect(formatTimestamp("whenever")).toBe("whenever");
  });
});
