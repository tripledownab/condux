import { useListMyOrgs, useListProjects } from "@/src/api/generated/condux";
import type { Org, ProjectRecord } from "@/src/api/generated/model";
import { useSelectedOrg } from "@/src/orgs/selected-org";
import { useSelectedProject } from "@/src/projects/selected-project";

// The outcomes of resolving the current project. A distinct case for each so the UI never silently
// blanks a missing step (loading vs error vs no-org vs no-projects vs ready).
export enum ProjectStatus {
  Loading = "loading",
  Error = "error",
  NoOrg = "no-org",
  NoProjects = "no-projects",
  Ready = "ready",
}

export type CurrentProjectState =
  | { status: ProjectStatus.Loading }
  | { status: ProjectStatus.Error }
  | { status: ProjectStatus.NoOrg }
  | { status: ProjectStatus.NoProjects; org: Org }
  | { status: ProjectStatus.Ready; org: Org; project: ProjectRecord };

// The dashboard is scoped to one project at a time: the org the OrgSwitcher selected (see useSelectedOrg)
// and the project the ProjectSwitcher selected (see useSelectedProject) — each falling back to the first
// membership / the org's first project when nothing is selected or the stored id is no longer valid.
export function useCurrentProject(): CurrentProjectState {
  const { selectedOrgId } = useSelectedOrg();
  const { selectedProjectId } = useSelectedProject();
  const orgs = useListMyOrgs();
  const memberships = orgs.data?.data ?? [];
  const org =
    memberships.find((membership) => membership.org.id === selectedOrgId)?.org ??
    memberships[0]?.org;
  // Only fetch projects once we know the org; the id is unused while disabled, so 0 is a safe stand-in.
  const projects = useListProjects(org?.id ?? 0, { query: { enabled: Boolean(org) } });

  if (orgs.isPending) {
    return { status: ProjectStatus.Loading };
  }
  if (orgs.isError) {
    return { status: ProjectStatus.Error };
  }
  if (!org) {
    return { status: ProjectStatus.NoOrg };
  }
  if (projects.isPending) {
    return { status: ProjectStatus.Loading };
  }
  if (projects.isError) {
    return { status: ProjectStatus.Error };
  }

  const list = projects.data?.data ?? [];
  const project = list.find((candidate) => candidate.id === selectedProjectId) ?? list[0];
  if (!project) {
    return { status: ProjectStatus.NoProjects, org };
  }
  return { status: ProjectStatus.Ready, org, project };
}
