"use client";

import { useTranslations } from "next-intl";
import { ALERT_TOKEN_NAMES } from "@/src/alerts/alert-format";
import { type EditorToken, TokenEditor } from "./token-editor";

// The alert-channel message-template field: the TokenEditor with the alert token palette + a hint. Token
// labels resolve under "settings.channels.token"; the "restore default" action lives in the dialog footer.
export function TemplateField({
  value,
  onChange,
}: {
  value: string;
  onChange: (value: string) => void;
}) {
  const translate = useTranslations("settings.channels");
  const tokens: EditorToken[] = ALERT_TOKEN_NAMES.map((name) => ({
    name,
    label: translate(`token.${name}`),
  }));

  return (
    <div className="flex w-full flex-col gap-1.5 text-sm">
      <TokenEditor
        value={value}
        onChange={onChange}
        tokens={tokens}
        ariaLabel={translate("template")}
      />
      <p className="text-xs text-muted-foreground">{translate("templateHint")}</p>
    </div>
  );
}
