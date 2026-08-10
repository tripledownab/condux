"use client";

import { useListMyOrgs } from "@/src/api/generated/condux";
import type { Org } from "@/src/api/generated/model";
import { useSelectedOrg } from "./selected-org";

// The outcomes of resolving the current org (a distinct case for each, so the UI never blanks a missing
// step). Mirrors useCurrentProject's org resolution but without needing a project.
export enum OrgStatus {
  Loading = "loading",
  Error = "error",
  NoOrg = "no-org",
  Ready = "ready",
}

export type CurrentOrgState =
  | { status: OrgStatus.Loading }
  | { status: OrgStatus.Error }
  | { status: OrgStatus.NoOrg }
  | { status: OrgStatus.Ready; org: Org; role: string };

// The org the dashboard is scoped to (the OrgSwitcher's selection, falling back to the first membership)
// plus the caller's role in it. Used by the org-scoped settings tabs (General, Members).
export function useCurrentOrg(): CurrentOrgState {
  const { selectedOrgId } = useSelectedOrg();
  const orgs = useListMyOrgs();

  if (orgs.isPending) {
    return { status: OrgStatus.Loading };
  }
  if (orgs.isError) {
    return { status: OrgStatus.Error };
  }
  const memberships = orgs.data?.data ?? [];
  const current =
    memberships.find((membership) => membership.org.id === selectedOrgId) ?? memberships[0];
  if (!current) {
    return { status: OrgStatus.NoOrg };
  }
  return { status: OrgStatus.Ready, org: current.org, role: current.role };
}
