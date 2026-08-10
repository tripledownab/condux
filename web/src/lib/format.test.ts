import { describe, expect, it } from "vitest";
import { formatCompactNumber, formatUsd } from "./format";

describe("formatUsd", () => {
  it("shows a dollar amount in cents precision", () => {
    expect(formatUsd(17.5)).toBe("$17.50");
    expect(formatUsd(0)).toBe("$0.00");
    expect(formatUsd(1234.5)).toBe("$1,234.50");
  });
});

describe("formatCompactNumber", () => {
  it("compacts large counts and keeps small ones exact", () => {
    expect(formatCompactNumber(300)).toBe("300");
    expect(formatCompactNumber(1_200_000)).toBe("1.2M");
    expect(formatCompactNumber(300_000)).toBe("300K");
  });
});
