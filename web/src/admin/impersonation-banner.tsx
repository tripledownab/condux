"use client";

import { useQueryClient } from "@tanstack/react-query";
import { useTranslations } from "next-intl";
import { getMeQueryKey, useAdminStopImpersonation, useMe } from "@/src/api/generated/condux";
import { ROUTES } from "@/src/routes";

// Shown across the whole authed shell while a platform admin is in a read-only "view as org" session
// (ADR-0027). Reads the state from /api/auth/me (no prop drilling); Exit stops the session and hard-reloads
// home so every org-scoped query refetches under the admin's own identity. Hidden when not impersonating.
export function ImpersonationBanner() {
  const translate = useTranslations("admin.impersonation");
  const queryClient = useQueryClient();
  const me = useMe();
  const stop = useAdminStopImpersonation();
  const impersonation = me.data?.data?.impersonation;

  if (!impersonation) {
    return null;
  }

  return (
    <div className="flex items-center justify-between gap-3 border-b border-warning bg-warning/15 px-4 py-2 text-sm text-foreground">
      <span className="flex items-center gap-2">
        <span className="inline-block h-2 w-2 shrink-0 rounded-full bg-warning" />
        {translate("viewing", { org: impersonation.orgName })}
      </span>
      <button
        type="button"
        disabled={stop.isPending}
        onClick={() =>
          stop.mutate(undefined, {
            onSuccess: async () => {
              await queryClient.invalidateQueries({ queryKey: getMeQueryKey() });
              window.location.assign(ROUTES.home);
            },
          })
        }
        className="rounded-md border border-border bg-card px-2 py-1 text-xs font-medium transition-colors hover:bg-secondary disabled:opacity-50"
      >
        {translate("exit")}
      </button>
    </div>
  );
}
