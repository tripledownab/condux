"use client";

import { useTranslations } from "next-intl";
import { SECONDARY_BUTTON_CLASS } from "@/src/components/form";

// The state of a per-channel test send, driving the inline status label next to the button.
export enum TestStatus {
  Idle = "idle",
  Sending = "sending",
  Sent = "sent",
  Failed = "failed",
  NotConfigured = "not-configured",
}

// Maps a TestChannelResponse (delivered + optional error) to a status. "channel_not_configured" (no notifier
// registered, e.g. email without SMTP) is called out distinctly from a genuine delivery failure.
export function testStatus(delivered: boolean, error: string | null): TestStatus {
  if (delivered) {
    return TestStatus.Sent;
  }
  return error === "channel_not_configured" ? TestStatus.NotConfigured : TestStatus.Failed;
}

const STATUS_KEYS: Record<TestStatus, string | null> = {
  [TestStatus.Idle]: null,
  [TestStatus.Sending]: "testSending",
  [TestStatus.Sent]: "testSent",
  [TestStatus.Failed]: "testFailed",
  [TestStatus.NotConfigured]: "testNotConfigured",
};

// A "Send test" button plus an aria-live status label for the last attempt. Namespace: settings.channels.
export function TestChannelButton({ status, onSend }: { status: TestStatus; onSend: () => void }) {
  const translate = useTranslations("settings.channels");
  const statusKey = STATUS_KEYS[status];
  return (
    <span className="flex items-center gap-2">
      {statusKey ? (
        <span aria-live="polite" className="text-xs text-muted-foreground">
          {translate(statusKey)}
        </span>
      ) : null}
      <button
        type="button"
        onClick={onSend}
        disabled={status === TestStatus.Sending}
        className={SECONDARY_BUTTON_CLASS}
      >
        {translate("sendTest")}
      </button>
    </span>
  );
}
