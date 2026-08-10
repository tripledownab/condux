"use client";

import { useQueryClient } from "@tanstack/react-query";
import { getMeQueryKey, useAdminImpersonateOrg } from "@/src/api/generated/condux";
import { ROUTES } from "@/src/routes";

// Starts a read-only "view as org" session (ADR-0027) and, on success, refetches identity + hard-navigates
// home so every org-scoped query reloads under the view-as identity and the banner appears. Shared by the
// org detail button and the users-list action.
export function useImpersonateOrg() {
  const queryClient = useQueryClient();
  const impersonate = useAdminImpersonateOrg();

  const start = (orgId: number) =>
    impersonate.mutate(
      { orgId },
      {
        onSuccess: async () => {
          await queryClient.invalidateQueries({ queryKey: getMeQueryKey() });
          window.location.assign(ROUTES.home);
        },
      },
    );

  return { start, pending: impersonate.isPending, isError: impersonate.isError };
}
