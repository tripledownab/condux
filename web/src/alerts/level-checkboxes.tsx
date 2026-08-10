"use client";

import { useTranslations } from "next-intl";
import { CheckboxField } from "@/src/components/checkbox-field";
import { CONTROL_ROW_CLASS } from "@/src/components/form";
import { levelMeta } from "@/src/issues/issue-format";
import { cn } from "@/src/lib/utils";
import { LEVEL_OPTIONS } from "./alert-format";

// The per-level severity picker: a checkbox per selectable level, toggling its membership in `levels`. A
// rule fires on exactly the checked levels (not a >= threshold), so any overlap between rules is explicit.
export function LevelCheckboxes({
  levels,
  onChange,
}: {
  levels: number[];
  onChange: (levels: number[]) => void;
}) {
  const translate = useTranslations("settings.alerts");
  const translateLevel = useTranslations("issues.level");
  const toggle = (level: number, on: boolean) =>
    onChange(on ? [...levels, level] : levels.filter((value) => value !== level));

  return (
    <div className="flex flex-col gap-1 text-sm">
      <span className="text-xs uppercase text-muted-foreground">{translate("levels")}</span>
      <div className={cn(CONTROL_ROW_CLASS, "flex-wrap")}>
        {LEVEL_OPTIONS.map((level) => (
          <CheckboxField
            key={level}
            checked={levels.includes(level)}
            onChange={(on) => toggle(level, on)}
          >
            {translateLevel(levelMeta(level).key)}
          </CheckboxField>
        ))}
      </div>
    </div>
  );
}
