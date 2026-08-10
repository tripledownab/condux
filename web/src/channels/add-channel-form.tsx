"use client";

import { useTranslations } from "next-intl";
import { useState } from "react";
import { FIELD_CLASS, PRIMARY_BUTTON_CLASS } from "@/src/components/form";
import { Combobox } from "@/src/components/ui/combobox";
import { CHANNEL_OPTIONS } from "./format";

// The card-wrapped add-channel form shared by both settings tabs: a channel-type select + a target input,
// submitting on Enter. The parent owns the mutation via onAdd; the form clears its target after submit.
// New alert channels start with the default message template, customized later from the channel row's modal.
export function AddChannelForm({
  onAdd,
  pending,
}: {
  onAdd: (channel: number, target: string) => void;
  pending: boolean;
}) {
  const translate = useTranslations("settings.channels");
  const tCommon = useTranslations("common");
  const [channel, setChannel] = useState<number>(CHANNEL_OPTIONS[0].value);
  const [target, setTarget] = useState("");
  const channelOptions = CHANNEL_OPTIONS.map((option) => ({
    value: String(option.value),
    label: translate(option.key),
  }));

  const submit = (event: React.FormEvent) => {
    event.preventDefault();
    const trimmed = target.trim();
    if (trimmed === "") {
      return;
    }
    onAdd(channel, trimmed);
    setTarget("");
  };

  return (
    <form
      onSubmit={submit}
      className="flex flex-wrap items-end gap-2 rounded-lg border border-border bg-card p-4"
    >
      <div className="flex flex-col gap-1 text-sm">
        <span className="text-xs uppercase text-muted-foreground">{translate("channelLabel")}</span>
        <Combobox
          value={String(channel)}
          onValueChange={(value) => setChannel(Number(value))}
          options={channelOptions}
          aria-label={translate("channelLabel")}
          searchPlaceholder={tCommon("comboboxSearch")}
          emptyText={tCommon("comboboxEmpty")}
        />
      </div>
      <label className="flex flex-1 flex-col gap-1 text-sm">
        <span className="text-xs uppercase text-muted-foreground">{translate("targetLabel")}</span>
        <input
          aria-label={translate("targetLabel")}
          value={target}
          onChange={(event) => setTarget(event.target.value)}
          placeholder={translate("targetPlaceholder")}
          className={FIELD_CLASS}
        />
      </label>
      <button type="submit" disabled={pending} className={PRIMARY_BUTTON_CLASS}>
        {translate("add")}
      </button>
    </form>
  );
}
