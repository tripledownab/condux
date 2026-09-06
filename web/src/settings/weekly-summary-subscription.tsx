"use client";

import { useQueryClient } from "@tanstack/react-query";
import { useTranslations } from "next-intl";
import { useId } from "react";
import {
  getMeQueryKey,
  useMe,
  useUpdateWeeklySummarySubscription,
} from "@/src/api/generated/condux";

// The caller's own subscription to the weekly digest, a sibling of WeeklySummarySettings rather than part
// of it: that one is the org's schedule and needs admin, this is one person's inbox and any member sets
// their own. Keeping them apart is also what keeps either file readable.
//
// Stored server-side as an opt-out (default false = subscribed), so the checkbox is its inverse. The
// negation happens here, once, and nowhere else.
export function WeeklySummarySubscription() {
  const translate = useTranslations("settings.weeklySummary");
  const queryClient = useQueryClient();
  const me = useMe();
  const checkboxId = useId();
  const update = useUpdateWeeklySummarySubscription();

  const subscribed = !(me.data?.data.weeklySummaryOptOut ?? false);

  const toggle = (nextSubscribed: boolean) => {
    if (update.isPending) {
      return;
    }
    update.mutate(
      { data: { optOut: !nextSubscribed } },
      { onSuccess: () => queryClient.invalidateQueries({ queryKey: getMeQueryKey() }) },
    );
  };

  return (
    <div className="flex flex-col gap-1 border-border border-t pt-3">
      <label htmlFor={checkboxId} className="flex cursor-pointer items-center gap-2 text-sm">
        <input
          id={checkboxId}
          type="checkbox"
          className="size-4 accent-primary"
          checked={subscribed}
          disabled={update.isPending}
          onChange={(event) => toggle(event.target.checked)}
        />
        <span className="text-foreground">{translate("subscriptionLabel")}</span>
      </label>
      <p className="text-muted-foreground text-xs">{translate("subscriptionHint")}</p>
      {update.isError ? (
        <p role="alert" className="text-error text-sm">
          {translate("saveFailed")}
        </p>
      ) : null}
    </div>
  );
}
