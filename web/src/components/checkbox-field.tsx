"use client";

import { type ReactNode, useId } from "react";
import { Checkbox } from "@/src/components/ui/checkbox";

// A vertically-centered checkbox + label row (shadcn Checkbox). Generic, so any form can use it. The label
// is associated by id rather than wrapping the checkbox, so a click on the control toggles it exactly once.
export function CheckboxField({
  checked,
  onChange,
  children,
}: {
  checked: boolean;
  onChange: (checked: boolean) => void;
  children: ReactNode;
}) {
  const id = useId();
  return (
    <div className="flex items-center gap-2 text-sm text-foreground">
      <Checkbox id={id} checked={checked} onCheckedChange={(value) => onChange(value === true)} />
      <label htmlFor={id}>{children}</label>
    </div>
  );
}
