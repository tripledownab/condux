import { describe, expect, it } from "vitest";
import messages from "@/messages/en.json";
import { EVENT_OPTIONS } from "@/src/alerts/alert-format";
import { readBackendEnum } from "@/src/test-utils/backend-enums";

describe("EVENT_OPTIONS", () => {
  // A rule's events travel as bare integers, so an event the backend adds alone could never be chosen
  // in the editor, and one the editor offers that the backend lacks would be refused on save.
  it("offers exactly the events the backend enum declares, each with a label", () => {
    expect(EVENT_OPTIONS.map((option) => option.value).sort()).toEqual(
      Object.values(readBackendEnum("Alerting/AlertModel.cs", "AlertEventType", 5)).sort(),
    );
    for (const option of EVENT_OPTIONS) {
      expect(messages.settings.alerts.event).toHaveProperty(option.key);
    }
  });
});
