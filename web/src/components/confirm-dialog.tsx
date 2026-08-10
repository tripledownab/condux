"use client";

import { Dialog as DialogPrimitive } from "radix-ui";
import type { ReactNode } from "react";
import { PRIMARY_BUTTON_CLASS, SECONDARY_BUTTON_CLASS } from "@/src/components/form";

// A generic confirm dialog for actions worth a second look (remove a member, cancel a subscription, start
// impersonating). Cross-cutting UI, so it lives in components/, not a feature folder. Mirrors the radix
// dialog shape of the Suggest-fix confirm. Set destructive for a red confirm button.
export function ConfirmDialog({
  open,
  onOpenChange,
  title,
  body,
  confirmLabel,
  cancelLabel,
  pending = false,
  destructive = false,
  onConfirm,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  title: string;
  body: ReactNode;
  confirmLabel: string;
  cancelLabel: string;
  pending?: boolean;
  destructive?: boolean;
  onConfirm: () => void;
}) {
  const confirmClass = destructive
    ? "rounded-md bg-destructive px-3 py-2 text-sm font-medium text-destructive-foreground transition-colors hover:opacity-90 focus:outline-none focus-visible:ring-2 focus-visible:ring-destructive disabled:opacity-50"
    : PRIMARY_BUTTON_CLASS;

  return (
    <DialogPrimitive.Root open={open} onOpenChange={onOpenChange}>
      <DialogPrimitive.Portal>
        <DialogPrimitive.Overlay className="fixed inset-0 z-50 bg-black/50" />
        <DialogPrimitive.Content className="fixed left-1/2 top-1/2 z-50 w-[90vw] max-w-md -translate-x-1/2 -translate-y-1/2 rounded-lg border border-border bg-card p-6 shadow-lg focus:outline-none">
          <DialogPrimitive.Title className="font-heading text-lg font-semibold text-foreground">
            {title}
          </DialogPrimitive.Title>
          <DialogPrimitive.Description className="mt-2 text-sm text-muted-foreground">
            {body}
          </DialogPrimitive.Description>
          <div className="mt-5 flex justify-end gap-2">
            <DialogPrimitive.Close className={SECONDARY_BUTTON_CLASS}>
              {cancelLabel}
            </DialogPrimitive.Close>
            <button type="button" onClick={onConfirm} disabled={pending} className={confirmClass}>
              {confirmLabel}
            </button>
          </div>
        </DialogPrimitive.Content>
      </DialogPrimitive.Portal>
    </DialogPrimitive.Root>
  );
}
