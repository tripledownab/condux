"use client";

import Link from "next/link";
import { useTranslations } from "next-intl";
import { Dialog as DialogPrimitive } from "radix-ui";
import { PRIMARY_BUTTON_CLASS, SECONDARY_BUTTON_CLASS } from "@/src/components/form";
import { ROUTES } from "@/src/routes";

// Shown when a fix is requested for a project with no connected repository: the Conductor opens fixes
// as human-reviewed draft PRs, so it needs a linked GitHub repo. GitHub connection + repo linking live in
// the project's Repositories now (#134), so it points there. Radix Dialog gives focus trap + Escape close.
export function ConnectRepoDialog({
  open,
  onOpenChange,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
}) {
  const translate = useTranslations("issues.fix.connectRepo");
  return (
    <DialogPrimitive.Root open={open} onOpenChange={onOpenChange}>
      <DialogPrimitive.Portal>
        <DialogPrimitive.Overlay className="fixed inset-0 z-50 bg-black/50" />
        <DialogPrimitive.Content className="fixed left-1/2 top-1/2 z-50 w-[90vw] max-w-md -translate-x-1/2 -translate-y-1/2 rounded-lg border border-border bg-card p-6 shadow-lg focus:outline-none">
          <DialogPrimitive.Title className="font-heading text-lg font-semibold text-foreground">
            {translate("title")}
          </DialogPrimitive.Title>
          <DialogPrimitive.Description className="mt-2 text-sm text-muted-foreground">
            {translate("body")}
          </DialogPrimitive.Description>
          <div className="mt-5 flex justify-end gap-2">
            <DialogPrimitive.Close className={SECONDARY_BUTTON_CLASS}>
              {translate("ok")}
            </DialogPrimitive.Close>
            <Link href={ROUTES.projects} className={PRIMARY_BUTTON_CLASS}>
              {translate("connect")}
            </Link>
          </div>
        </DialogPrimitive.Content>
      </DialogPrimitive.Portal>
    </DialogPrimitive.Root>
  );
}
