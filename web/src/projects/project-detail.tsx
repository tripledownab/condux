"use client";

import { useQueryClient } from "@tanstack/react-query";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useFormatter, useTranslations } from "next-intl";
import { useState } from "react";
import {
  getGetProjectQueryKey,
  useDeleteProject,
  useGetProject,
  useUpdateProject,
} from "@/src/api/generated/condux";
import type { ProjectRecord } from "@/src/api/generated/model";
import { FIELD_CLASS, PRIMARY_BUTTON_CLASS, SECONDARY_BUTTON_CLASS } from "@/src/components/form";
import { Notice } from "@/src/components/notice";
import { canManageOrg, OrgStatus, useCurrentOrg } from "@/src/orgs/current-org";
import { usePlatformLabel } from "@/src/projects/platforms";
import { ROUTES } from "@/src/routes";
import { ProjectSections } from "./project-sections";

// A single project's page: an overview card (rename/delete, admin+) above the tabbed management sections
// in ProjectSections. Fetches one project via the lean getProject endpoint (by public UUID, #125) rather
// than the whole org list.
export function ProjectDetail({ publicId }: { publicId: string }) {
  const translate = useTranslations("projects");
  const current = useCurrentOrg();
  const project = useGetProject(publicId);

  if (project.isPending || current.status === OrgStatus.Loading) {
    return <Notice>{translate("loading")}</Notice>;
  }
  // A 404 (unknown project, or not the caller's org) and any load error land here.
  const record = project.data?.status === 200 ? project.data.data : null;
  if (project.isError || record === null) {
    return <Notice>{translate("notFound")}</Notice>;
  }

  const canManage = current.status === OrgStatus.Ready && canManageOrg(current.role);

  return (
    <div className="flex flex-col gap-8">
      <div>
        <Link
          href={ROUTES.projects}
          className="text-sm text-muted-foreground hover:text-foreground"
        >
          ← {translate("back")}
        </Link>
      </div>
      <Overview project={record} canManage={canManage} />
      <ProjectSections
        projectId={record.id}
        publicId={record.publicId}
        projectName={record.name}
        orgId={current.status === OrgStatus.Ready ? current.org.id : null}
        canManage={canManage}
      />
    </div>
  );
}

function Overview({ project, canManage }: { project: ProjectRecord; canManage: boolean }) {
  const translate = useTranslations("projects");
  const platformLabel = usePlatformLabel();
  const format = useFormatter();
  const router = useRouter();
  const queryClient = useQueryClient();
  const updateProject = useUpdateProject();
  const deleteProject = useDeleteProject();
  const [editing, setEditing] = useState(false);
  const [name, setName] = useState(project.name);
  const [confirmingDelete, setConfirmingDelete] = useState(false);

  const saveRename = () => {
    if (name.trim() === "") {
      return;
    }
    updateProject.mutate(
      { projectId: project.publicId, data: { name: name.trim(), platform: project.platform } },
      {
        onSuccess: () => {
          setEditing(false);
          queryClient.invalidateQueries({ queryKey: getGetProjectQueryKey(project.publicId) });
        },
      },
    );
  };

  const remove = () =>
    deleteProject.mutate(
      { projectId: project.publicId },
      { onSuccess: () => router.push(ROUTES.projects) },
    );

  return (
    <section className="rounded-lg border border-border bg-card p-6">
      <div className="flex items-start justify-between gap-3">
        {editing ? (
          <div className="flex flex-1 items-center gap-2">
            <input
              aria-label={translate("name")}
              value={name}
              onChange={(event) => setName(event.target.value)}
              className={FIELD_CLASS}
            />
            <button
              type="button"
              onClick={saveRename}
              disabled={updateProject.isPending}
              className={PRIMARY_BUTTON_CLASS}
            >
              {translate("save")}
            </button>
            <button
              type="button"
              onClick={() => {
                setEditing(false);
                setName(project.name);
              }}
              className={SECONDARY_BUTTON_CLASS}
            >
              {translate("cancel")}
            </button>
          </div>
        ) : (
          <h1 className="font-heading text-2xl font-semibold text-foreground">{project.name}</h1>
        )}
        {canManage && !editing ? (
          <div className="flex shrink-0 items-center gap-2">
            {confirmingDelete ? (
              <>
                <span className="text-xs text-muted-foreground">{translate("confirmDelete")}</span>
                <button
                  type="button"
                  onClick={remove}
                  disabled={deleteProject.isPending}
                  className={SECONDARY_BUTTON_CLASS}
                >
                  {translate("confirm")}
                </button>
                <button
                  type="button"
                  onClick={() => setConfirmingDelete(false)}
                  className={SECONDARY_BUTTON_CLASS}
                >
                  {translate("cancel")}
                </button>
              </>
            ) : (
              <>
                <button
                  type="button"
                  onClick={() => setEditing(true)}
                  className={SECONDARY_BUTTON_CLASS}
                >
                  {translate("rename")}
                </button>
                <button
                  type="button"
                  onClick={() => setConfirmingDelete(true)}
                  className={SECONDARY_BUTTON_CLASS}
                >
                  {translate("delete")}
                </button>
              </>
            )}
          </div>
        ) : null}
      </div>

      <dl className="mt-4 grid grid-cols-[auto_1fr] gap-x-6 gap-y-1.5 text-sm">
        <dt className="text-muted-foreground">{translate("platform")}</dt>
        <dd className="text-foreground">{platformLabel(project.platform)}</dd>
        <dt className="text-muted-foreground">{translate("created")}</dt>
        <dd className="text-foreground">
          {format.dateTime(new Date(project.createdAt), {
            month: "short",
            day: "numeric",
            year: "numeric",
          })}
        </dd>
      </dl>
    </section>
  );
}
