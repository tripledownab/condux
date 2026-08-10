import type { ReactNode } from "react";

// Shared form styling + a labeled field, so the auth and settings forms look and behave the same.
export const FIELD_CLASS =
  "rounded-md border border-border bg-background px-3 py-2 text-sm text-foreground focus:outline-none focus-visible:ring-2 focus-visible:ring-primary";

export const PRIMARY_BUTTON_CLASS =
  "rounded-md bg-primary px-3 py-2 text-sm font-medium text-primary-foreground transition-colors hover:opacity-90 focus:outline-none focus-visible:ring-2 focus-visible:ring-primary disabled:opacity-50";

export const SECONDARY_BUTTON_CLASS =
  "rounded-md border border-border px-3 py-1.5 text-sm text-foreground transition-colors hover:bg-secondary disabled:opacity-50";

// A row of controls (e.g. checkboxes) sized to a FIELD_CLASS input's height: the py-2 + transparent border
// mirror the input's box, so when a form row bottom-aligns, the row's controls center on the input and any
// caption above them lines up with the input's caption.
export const CONTROL_ROW_CLASS = "flex items-center gap-3 border border-transparent py-2";

// Associates a label with its control via htmlFor/id so the field is accessible.
export function Field({ id, label, children }: { id: string; label: string; children: ReactNode }) {
  return (
    <div className="flex flex-col gap-1.5">
      <label htmlFor={id} className="text-sm text-muted-foreground">
        {label}
      </label>
      {children}
    </div>
  );
}
