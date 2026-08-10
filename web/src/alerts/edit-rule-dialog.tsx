"use client";

import { useTranslations } from "next-intl";
import { Dialog as DialogPrimitive } from "radix-ui";
import { useEffect, useState } from "react";
import { useUpdateAlertRule } from "@/src/api/generated/condux";
import type { AlertRuleResponse } from "@/src/api/generated/model";
import { FIELD_CLASS, PRIMARY_BUTTON_CLASS, SECONDARY_BUTTON_CLASS } from "@/src/components/form";
import { EventCheckboxes } from "./event-checkboxes";
import { LevelCheckboxes } from "./level-checkboxes";

// Modal to edit a rule's settings (name, events, levels). updateAlertRule carries the rule's current enabled
// state so an edit never flips it; Save closes and invalidates via onSaved. Drafts reset each time it opens.
export function EditRuleDialog({
  rule,
  projectId,
  open,
  onOpenChange,
  onSaved,
}: {
  rule: AlertRuleResponse;
  projectId: number;
  open: boolean;
  onOpenChange: (open: boolean) => void;
  onSaved: () => void;
}) {
  const translate = useTranslations("settings.alerts");
  const updateRule = useUpdateAlertRule();
  const [name, setName] = useState(rule.name);
  const [events, setEvents] = useState<number[]>(rule.events);
  const [levels, setLevels] = useState<number[]>(rule.levels);

  useEffect(() => {
    if (open) {
      setName(rule.name);
      setEvents(rule.events);
      setLevels(rule.levels);
    }
  }, [open, rule.name, rule.events, rule.levels]);

  const submit = (event: React.FormEvent) => {
    event.preventDefault();
    if (name.trim() === "" || levels.length === 0 || events.length === 0) {
      return;
    }
    updateRule.mutate(
      {
        projectId,
        ruleId: rule.id,
        data: { name: name.trim(), events, levels, enabled: rule.enabled },
      },
      {
        onSuccess: () => {
          onSaved();
          onOpenChange(false);
        },
      },
    );
  };

  return (
    <DialogPrimitive.Root open={open} onOpenChange={onOpenChange}>
      <DialogPrimitive.Portal>
        <DialogPrimitive.Overlay className="fixed inset-0 z-50 bg-black/50" />
        <DialogPrimitive.Content className="fixed left-1/2 top-1/2 z-50 w-[90vw] max-w-md -translate-x-1/2 -translate-y-1/2 rounded-lg border border-border bg-card p-6 shadow-lg focus:outline-none">
          <DialogPrimitive.Title className="font-heading text-lg font-semibold text-foreground">
            {translate("editRule")}
          </DialogPrimitive.Title>
          <form onSubmit={submit} className="mt-4 flex flex-col gap-4">
            <label className="flex flex-col gap-1 text-sm">
              <span className="text-xs uppercase text-muted-foreground">{translate("name")}</span>
              <input
                value={name}
                onChange={(event) => setName(event.target.value)}
                className={FIELD_CLASS}
              />
            </label>
            <EventCheckboxes events={events} onChange={setEvents} />
            <LevelCheckboxes levels={levels} onChange={setLevels} />
            <div className="mt-1 flex justify-end gap-2">
              <DialogPrimitive.Close className={SECONDARY_BUTTON_CLASS}>
                {translate("cancel")}
              </DialogPrimitive.Close>
              <button
                type="submit"
                disabled={updateRule.isPending}
                className={PRIMARY_BUTTON_CLASS}
              >
                {updateRule.isPending ? translate("saving") : translate("save")}
              </button>
            </div>
          </form>
        </DialogPrimitive.Content>
      </DialogPrimitive.Portal>
    </DialogPrimitive.Root>
  );
}
