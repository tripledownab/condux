"use client";

import { useTranslations } from "next-intl";
import type { ReactNode } from "react";
import { Badge } from "@/src/components/ui/badge";
import { truncate } from "@/src/lib/strings";
import { channelKey } from "./format";

// A channel target (a webhook URL, email, etc.) can be long; cap the displayed text so it does not wrap
// the row on narrow screens. The full value stays on the title tooltip.
const MAX_TARGET_CHARS = 32;

// A delivery channel: its transport type as an uppercase caption, its target in <code>, and an action slot
// (test + remove buttons) on the right. Shared by the alert-rule and org-notification channel lists so both
// read identically. Namespace: settings.channels.
export function ChannelRow({
  channel,
  target,
  children,
}: {
  channel: number;
  target: string;
  children?: ReactNode;
}) {
  const translate = useTranslations("settings.channels");
  return (
    <li className="flex items-center justify-between gap-3 text-sm">
      <span className="text-foreground">
        <Badge variant="secondary" className="uppercase">
          {translate(channelKey(channel))}
        </Badge>{" "}
        <code title={target} className="text-muted-foreground">
          {truncate(target, MAX_TARGET_CHARS)}
        </code>
      </span>
      {children ? <span className="flex items-center gap-3">{children}</span> : null}
    </li>
  );
}

// The channel list wrapper: a subtle empty note, or the rows. Callers pass ChannelRow children.
export function ChannelList({ isEmpty, children }: { isEmpty: boolean; children: ReactNode }) {
  const translate = useTranslations("settings.channels");
  if (isEmpty) {
    return <p className="text-xs text-muted-foreground">{translate("empty")}</p>;
  }
  return <ul className="flex flex-col gap-2">{children}</ul>;
}
