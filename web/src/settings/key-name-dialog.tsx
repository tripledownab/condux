"use client";

import { useTranslations } from "next-intl";
import { Dialog as DialogPrimitive } from "radix-ui";
import { useEffect, useState } from "react";
import { FIELD_CLASS, PRIMARY_BUTTON_CLASS, SECONDARY_BUTTON_CLASS } from "@/src/components/form";

// Modal to name a DSN key — used both to create a new key and to rename an existing one (only the title +
// initial value + submit label differ). The parent owns the mutation via onSubmit, which closes the dialog
// on success. The name resets to initialName each time it opens; submit is disabled until it is non-empty.
export function KeyNameDialog({
  open,
  onOpenChange,
  title,
  initialName,
  submitLabel,
  pending,
  onSubmit,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  title: string;
  initialName: string;
  submitLabel: string;
  pending: boolean;
  onSubmit: (name: string) => void;
}) {
  const translate = useTranslations("settings.keys");
  const [name, setName] = useState(initialName);

  useEffect(() => {
    if (open) {
      setName(initialName);
    }
  }, [open, initialName]);

  const submit = (event: React.FormEvent) => {
    event.preventDefault();
    const trimmed = name.trim();
    if (trimmed === "") {
      return;
    }
    onSubmit(trimmed);
  };

  return (
    <DialogPrimitive.Root open={open} onOpenChange={onOpenChange}>
      <DialogPrimitive.Portal>
        <DialogPrimitive.Overlay className="fixed inset-0 z-50 bg-black/50" />
        <DialogPrimitive.Content className="fixed left-1/2 top-1/2 z-50 w-[90vw] max-w-md -translate-x-1/2 -translate-y-1/2 rounded-lg border border-border bg-card p-6 shadow-lg focus:outline-none">
          <DialogPrimitive.Title className="font-heading text-lg font-semibold text-foreground">
            {title}
          </DialogPrimitive.Title>
          <form onSubmit={submit} className="mt-4 flex flex-col gap-3">
            <label className="flex flex-col gap-1 text-sm">
              <span className="text-xs uppercase text-muted-foreground">
                {translate("nameLabel")}
              </span>
              <input
                aria-label={translate("nameLabel")}
                value={name}
                onChange={(event) => setName(event.target.value)}
                placeholder={translate("namePlaceholder")}
                maxLength={100}
                className={FIELD_CLASS}
              />
            </label>
            <div className="mt-1 flex justify-end gap-2">
              <DialogPrimitive.Close className={SECONDARY_BUTTON_CLASS}>
                {translate("cancel")}
              </DialogPrimitive.Close>
              <button
                type="submit"
                disabled={pending || name.trim() === ""}
                className={PRIMARY_BUTTON_CLASS}
              >
                {submitLabel}
              </button>
            </div>
          </form>
        </DialogPrimitive.Content>
      </DialogPrimitive.Portal>
    </DialogPrimitive.Root>
  );
}
