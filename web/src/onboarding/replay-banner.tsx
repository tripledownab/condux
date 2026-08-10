"use client";

import { useTranslations } from "next-intl";
import { useReplayOnboarding } from "./replay-onboarding";

// Dev-only marker shown on the onboarding page while the replay flag is on, so it is obvious why the
// app is gated to onboarding despite an org already existing. Renders nothing in production or when off.
export function ReplayBanner() {
  const translate = useTranslations("onboarding");
  const { replaying, setReplaying } = useReplayOnboarding();

  if (!replaying) {
    return null;
  }

  return (
    <div className="mb-4 flex items-center justify-between gap-3 rounded-md border border-dashed border-border bg-secondary/40 px-3 py-2 text-xs text-muted-foreground">
      <span>{translate("replayBanner")}</span>
      <button
        type="button"
        onClick={() => setReplaying(false)}
        className="shrink-0 font-medium text-primary hover:underline"
      >
        {translate("replayExit")}
      </button>
    </div>
  );
}
