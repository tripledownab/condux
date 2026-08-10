"use client";

import Link from "next/link";
import { useTranslations } from "next-intl";
import { useListProjects } from "@/src/api/generated/condux";
import type { ProjectRecord } from "@/src/api/generated/model";
import { Notice } from "@/src/components/notice";
import { OrgStatus, useCurrentOrg } from "@/src/orgs/current-org";
import { usePlatformLabel } from "@/src/projects/platforms";
import { projectDetailPath } from "@/src/routes";
import { CreateProjectForm } from "@/src/settings/create-project-form";

// The top-level Projects index: every project in the org as a link to its detail page, plus a create
// form for admins. Per-project management (rename, delete, DSN keys) lives on the detail page.
export function ProjectsIndex() {
  const translate = useTranslations("projects");
  const current = useCurrentOrg();

  if (current.status === OrgStatus.Loading) {
    return <Notice>{translate("loading")}</Notice>;
  }
  if (current.status === OrgStatus.Error) {
    return <Notice>{translate("error")}</Notice>;
  }
  if (current.status === OrgStatus.NoOrg) {
    return <Notice>{translate("noOrg")}</Notice>;
  }

  const canManage = current.role === "owner" || current.role === "admin";
  return (
    <div className="flex flex-col gap-8">
      <div>
        <h1 className="font-heading text-2xl font-semibold text-foreground">
          {translate("title")}
        </h1>
        <p className="mt-1 text-sm text-muted-foreground">{translate("description")}</p>
      </div>
      <ProjectList orgId={current.org.id} />
      {canManage ? <CreateProjectForm orgId={current.org.id} /> : null}
    </div>
  );
}

function ProjectList({ orgId }: { orgId: number }) {
  const translate = useTranslations("projects");
  const projects = useListProjects(orgId);

  if (projects.isPending) {
    return <Notice>{translate("loading")}</Notice>;
  }
  if (projects.isError) {
    return <Notice>{translate("error")}</Notice>;
  }
  const list = projects.data?.data ?? [];
  if (list.length === 0) {
    return <Notice>{translate("empty")}</Notice>;
  }
  return (
    <ul className="flex flex-col gap-3">
      {list.map((project) => (
        <ProjectCard key={project.id} project={project} />
      ))}
    </ul>
  );
}

function ProjectCard({ project }: { project: ProjectRecord }) {
  const platformLabel = usePlatformLabel();
  return (
    <li>
      <Link
        href={projectDetailPath(project.publicId)}
        className="block rounded-lg border border-border bg-card p-4 transition-colors hover:border-ring"
      >
        <span className="text-sm font-medium text-foreground">{project.name}</span>
        <span className="ml-2 text-xs text-muted-foreground">
          {platformLabel(project.platform)}
        </span>
      </Link>
    </li>
  );
}
