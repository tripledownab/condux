"use client";

import { useTranslations } from "next-intl";
import { CheckboxField } from "@/src/components/checkbox-field";
import { CONTROL_ROW_CLASS } from "@/src/components/form";
import { cn } from "@/src/lib/utils";
import { EVENT_OPTIONS } from "./alert-format";

// The per-event picker: a checkbox per issue event a rule can fire on (new issue, regression, resolved,
// assigned), toggling its membership in `events`. A rule fires on exactly the checked events.
export function EventCheckboxes({
  events,
  onChange,
}: {
  events: number[];
  onChange: (events: number[]) => void;
}) {
  const translate = useTranslations("settings.alerts");
  const toggle = (event: number, on: boolean) =>
    onChange(on ? [...events, event] : events.filter((value) => value !== event));

  return (
    <div className="flex flex-col gap-1 text-sm">
      <span className="text-xs uppercase text-muted-foreground">{translate("events")}</span>
      <div className={cn(CONTROL_ROW_CLASS, "flex-wrap")}>
        {EVENT_OPTIONS.map((option) => (
          <CheckboxField
            key={option.value}
            checked={events.includes(option.value)}
            onChange={(on) => toggle(option.value, on)}
          >
            {translate(`event.${option.key}`)}
          </CheckboxField>
        ))}
      </div>
    </div>
  );
}
