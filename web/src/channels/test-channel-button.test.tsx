import { describe, expect, it } from "vitest";
import { TestStatus, testStatus } from "./test-channel-button";

describe("testStatus", () => {
  it("maps a delivered result to Sent", () => {
    expect(testStatus(true, null)).toBe(TestStatus.Sent);
  });

  it("distinguishes an unconfigured channel from a genuine failure", () => {
    expect(testStatus(false, "channel_not_configured")).toBe(TestStatus.NotConfigured);
    expect(testStatus(false, "Connection refused")).toBe(TestStatus.Failed);
    expect(testStatus(false, null)).toBe(TestStatus.Failed);
  });
});
