"use client";

import { useTranslations } from "next-intl";
import { Dialog as DialogPrimitive } from "radix-ui";
import { useEffect, useState } from "react";
import { ALERT_TEMPLATE_DEFAULT } from "@/src/alerts/alert-format";
import { PRIMARY_BUTTON_CLASS, SECONDARY_BUTTON_CLASS } from "@/src/components/form";
import { MarkdownHint } from "./markdown-hint";
import { TemplateField } from "./template-field";

// The per-channel message template editor in a modal, opened from the channel row so the row stays compact.
// Holds a draft (reset to the channel's saved template whenever it opens); Save hands the draft to the
// parent, which runs the updateAlertChannel mutation. `channel` is the transport type, shown as a markdown
// hint since Slack/Discord render different dialects.
export function TemplateDialog({
  open,
  onOpenChange,
  channel,
  currentTemplate,
  pending,
  onSave,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  channel: number;
  currentTemplate: string | null | undefined;
  pending: boolean;
  onSave: (template: string) => void;
}) {
  const translate = useTranslations("settings.channels");
  const [template, setTemplate] = useState(currentTemplate ?? ALERT_TEMPLATE_DEFAULT);

  // Reset the draft to the saved template each time the dialog opens.
  useEffect(() => {
    if (open) {
      setTemplate(currentTemplate ?? ALERT_TEMPLATE_DEFAULT);
    }
  }, [open, currentTemplate]);

  return (
    <DialogPrimitive.Root open={open} onOpenChange={onOpenChange}>
      <DialogPrimitive.Portal>
        <DialogPrimitive.Overlay className="fixed inset-0 z-50 bg-black/50" />
        <DialogPrimitive.Content className="fixed left-1/2 top-1/2 z-50 w-[90vw] max-w-lg -translate-x-1/2 -translate-y-1/2 rounded-lg border border-border bg-card p-6 shadow-lg focus:outline-none">
          <DialogPrimitive.Title className="font-heading text-lg font-semibold text-foreground">
            {translate("template")}
          </DialogPrimitive.Title>
          <div className="mt-4">
            <TemplateField value={template} onChange={setTemplate} />
          </div>
          <div className="mt-3">
            <MarkdownHint channel={channel} />
          </div>
          <div className="mt-5 flex justify-end gap-2">
            <button
              type="button"
              onClick={() => setTemplate(ALERT_TEMPLATE_DEFAULT)}
              className={SECONDARY_BUTTON_CLASS}
            >
              {translate("restoreDefault")}
            </button>
            <DialogPrimitive.Close className={SECONDARY_BUTTON_CLASS}>
              {translate("cancel")}
            </DialogPrimitive.Close>
            <button
              type="button"
              onClick={() => onSave(template)}
              disabled={pending}
              className={PRIMARY_BUTTON_CLASS}
            >
              {translate("save")}
            </button>
          </div>
        </DialogPrimitive.Content>
      </DialogPrimitive.Portal>
    </DialogPrimitive.Root>
  );
}
