import { describe, expect, it } from "vitest";
import { TestStatus, testStatus } from "./test-channel-button";

describe("testStatus", () => {
  it("maps a delivered result to Sent", () => {
    expect(testStatus(true, null)).toBe(TestStatus.Sent);
  });

  it("calls out the two reasons a person can act on", () => {
    expect(testStatus(false, "channel_not_configured")).toBe(TestStatus.NotConfigured);
    expect(testStatus(false, "invalid_target")).toBe(TestStatus.InvalidTarget);
  });

  it("treats every other reason as a plain failure", () => {
    // The server answers with one of a fixed set of reasons and never the underlying error, so an
    // unrecognised value means a newer server rather than a message worth showing someone.
    expect(testStatus(false, "delivery_failed")).toBe(TestStatus.Failed);
    expect(testStatus(false, "a_reason_added_later")).toBe(TestStatus.Failed);
    expect(testStatus(false, null)).toBe(TestStatus.Failed);
  });
});
