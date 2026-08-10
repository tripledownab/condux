"use client";

import { useTranslations } from "next-intl";
import { Dialog as DialogPrimitive } from "radix-ui";
import { useEffect, useState } from "react";
import { FIELD_CLASS, PRIMARY_BUTTON_CLASS, SECONDARY_BUTTON_CLASS } from "@/src/components/form";
import { Combobox } from "@/src/components/ui/combobox";
import { CHANNEL_OPTIONS } from "./format";

// Modal to add a delivery channel to a rule (type + target). The parent owns the mutation via onAdd (which
// closes the dialog on success). Fields reset each time it opens. A new channel starts with the default
// message template, customized later from its row's Template button.
export function AddChannelDialog({
  open,
  onOpenChange,
  pending,
  onAdd,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  pending: boolean;
  onAdd: (channel: number, target: string) => void;
}) {
  const translate = useTranslations("settings.channels");
  const tCommon = useTranslations("common");
  const [channel, setChannel] = useState<number>(CHANNEL_OPTIONS[0].value);
  const [target, setTarget] = useState("");
  const channelOptions = CHANNEL_OPTIONS.map((option) => ({
    value: String(option.value),
    label: translate(option.key),
  }));

  useEffect(() => {
    if (open) {
      setChannel(CHANNEL_OPTIONS[0].value);
      setTarget("");
    }
  }, [open]);

  const submit = (event: React.FormEvent) => {
    event.preventDefault();
    const trimmed = target.trim();
    if (trimmed === "") {
      return;
    }
    onAdd(channel, trimmed);
  };

  return (
    <DialogPrimitive.Root open={open} onOpenChange={onOpenChange}>
      <DialogPrimitive.Portal>
        <DialogPrimitive.Overlay className="fixed inset-0 z-50 bg-black/50" />
        <DialogPrimitive.Content className="fixed left-1/2 top-1/2 z-50 w-[90vw] max-w-md -translate-x-1/2 -translate-y-1/2 rounded-lg border border-border bg-card p-6 shadow-lg focus:outline-none">
          <DialogPrimitive.Title className="font-heading text-lg font-semibold text-foreground">
            {translate("addChannel")}
          </DialogPrimitive.Title>
          <form onSubmit={submit} className="mt-4 flex flex-col gap-3">
            <div className="flex flex-col gap-1 text-sm">
              <span className="text-xs uppercase text-muted-foreground">
                {translate("channelLabel")}
              </span>
              <Combobox
                value={String(channel)}
                onValueChange={(value) => setChannel(Number(value))}
                options={channelOptions}
                aria-label={translate("channelLabel")}
                searchPlaceholder={tCommon("comboboxSearch")}
                emptyText={tCommon("comboboxEmpty")}
              />
            </div>
            <label className="flex flex-col gap-1 text-sm">
              <span className="text-xs uppercase text-muted-foreground">
                {translate("targetLabel")}
              </span>
              <input
                aria-label={translate("targetLabel")}
                value={target}
                onChange={(event) => setTarget(event.target.value)}
                placeholder={translate("targetPlaceholder")}
                className={FIELD_CLASS}
              />
            </label>
            <div className="mt-1 flex justify-end gap-2">
              <DialogPrimitive.Close className={SECONDARY_BUTTON_CLASS}>
                {translate("cancel")}
              </DialogPrimitive.Close>
              <button type="submit" disabled={pending} className={PRIMARY_BUTTON_CLASS}>
                {translate("add")}
              </button>
            </div>
          </form>
        </DialogPrimitive.Content>
      </DialogPrimitive.Portal>
    </DialogPrimitive.Root>
  );
}
