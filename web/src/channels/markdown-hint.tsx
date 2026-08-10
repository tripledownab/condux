"use client";

import { useTranslations } from "next-intl";
import { CHANNEL_MARKDOWN } from "./format";

// A one-line note in the message-template editor telling the user which markdown the channel's transport
// renders (Slack mrkdwn vs Discord markdown vs plain text). The framing text is translated; the example is
// literal syntax shown in a <code> span so the exact characters (e.g. Slack's single-asterisk *bold*) read
// clearly. Nothing renders for an unknown channel type.
export function MarkdownHint({ channel }: { channel: number }) {
  const translate = useTranslations("settings.channels");
  const hint = CHANNEL_MARKDOWN[channel];
  if (!hint) {
    return null;
  }
  return (
    <p className="text-xs text-muted-foreground">
      {translate(hint.labelKey)}
      {hint.example ? (
        <code className="ml-1.5 rounded bg-muted px-1 py-0.5 text-foreground">{hint.example}</code>
      ) : null}
    </p>
  );
}
