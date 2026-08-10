"use client";

import { useTranslations } from "next-intl";
import { Dialog as DialogPrimitive } from "radix-ui";
import { Fragment, useState } from "react";
import { useKeyboardShortcuts } from "@/src/lib/use-keyboard-shortcuts";
import { ISSUE_SHORTCUTS } from "./keyboard-shortcuts";

// A "?" cheat sheet of the issue keyboard shortcuts, data-driven from the single ISSUE_SHORTCUTS registry
// so the list never drifts from what's wired. "?" toggles it (Escape also closes, via radix). Mounted
// once on the issues surface; the shortcut hook ignores "?" while a field is focused.
export function ShortcutsHelp() {
  const translate = useTranslations("issues.shortcuts");
  const [open, setOpen] = useState(false);
  useKeyboardShortcuts({ "?": () => setOpen((current) => !current) });

  return (
    <DialogPrimitive.Root open={open} onOpenChange={setOpen}>
      <DialogPrimitive.Portal>
        <DialogPrimitive.Overlay className="fixed inset-0 z-50 bg-black/50" />
        <DialogPrimitive.Content
          aria-describedby={undefined}
          className="fixed left-1/2 top-1/2 z-50 w-[90vw] max-w-sm -translate-x-1/2 -translate-y-1/2 rounded-lg border border-border bg-card p-6 shadow-lg focus:outline-none"
        >
          <DialogPrimitive.Title className="font-heading text-lg font-semibold text-foreground">
            {translate("title")}
          </DialogPrimitive.Title>
          <dl className="mt-4 grid grid-cols-[auto_1fr] items-center gap-x-6 gap-y-2 text-sm">
            {ISSUE_SHORTCUTS.map((shortcut) => (
              <Fragment key={shortcut.labelKey}>
                <dt className="flex gap-1">
                  {shortcut.keys.map((key) => (
                    <kbd
                      key={key}
                      className="rounded border border-border bg-background px-1.5 py-0.5 font-mono text-xs text-foreground"
                    >
                      {key}
                    </kbd>
                  ))}
                </dt>
                <dd className="text-muted-foreground">{translate(shortcut.labelKey)}</dd>
              </Fragment>
            ))}
          </dl>
        </DialogPrimitive.Content>
      </DialogPrimitive.Portal>
    </DialogPrimitive.Root>
  );
}
