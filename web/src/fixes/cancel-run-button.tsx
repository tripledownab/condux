"use client";

import { useTranslations } from "next-intl";
import { Button } from "@/src/components/ui/button";
import { readErrorCode } from "@/src/lib/api-errors";

// Cancels a fix run nothing holds, an issue fix or a CVE bump: the caller owns the mutation, this owns
// the button and why the server refused it. A run something still holds, or one already ended, is refused
// with fix_not_cancellable, which gets its own line because trying again will not help.
export function CancelRunButton({
  onCancel,
  pending,
  error,
  compact = false,
}: {
  onCancel: () => void;
  pending: boolean;
  error: unknown;
  compact?: boolean;
}) {
  const translate = useTranslations("fixes.detail");
  const refused =
    error == null
      ? null
      : readErrorCode(error) === "fix_not_cancellable"
        ? translate("cancelRefused")
        : translate("cancelFailed");
  return (
    <>
      <Button
        variant="secondary"
        size={compact ? "xs" : "default"}
        onClick={onCancel}
        disabled={pending}
      >
        {pending ? translate("cancelling") : translate("cancel")}
      </Button>
      {refused === null ? null : <span className="text-xs text-error">{refused}</span>}
    </>
  );
}
