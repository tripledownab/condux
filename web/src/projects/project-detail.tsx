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
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/src/components/ui/tabs";
import { OrgStatus, useCurrentOrg } from "@/src/orgs/current-org";
import { usePlatformLabel } from "@/src/projects/platforms";
import { ROUTES } from "@/src/routes";
import { DsnKeys } from "@/src/settings/dsn-keys";
import { McpTokens } from "@/src/settings/mcp-tokens";
import { ReleaseTokens } from "@/src/settings/release-tokens";
import { Releases } from "@/src/settings/releases";
import { Repositories } from "@/src/settings/repositories";
import { useGithubConnection } from "@/src/settings/use-github-connection";

// The management sections beneath the overview card, one per tab, so GitHub + releases + keys do not
// crowd a single scroll.
enum ProjectTab {
  GitHub = "github",
  Releases = "releases",
  Dsn = "dsn",
  Mcp = "mcp",
}

// A single project's page: an overview card (rename/delete, admin+) above tabbed management sections —
// GitHub (connect + repositories), Releases, and DSN keys. Fetches one project via the lean getProject
// endpoint (by public UUID, #125) rather than the whole org list.
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

  const canManage =
    current.status === OrgStatus.Ready && (current.role === "owner" || current.role === "admin");

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
        canManage={canManage}
      />
    </div>
  );
}

// The project's management surface, tabbed beneath the overview card: GitHub (connect + repositories),
// Releases, and DSN keys. Each panel's data loads only while its tab is mounted (Radix unmounts the
// inactive panels), so opening the page does not fire every section's queries at once.
function ProjectSections({
  projectId,
  publicId,
  projectName,
  canManage,
}: {
  projectId: number;
  publicId: string;
  projectName: string;
  canManage: boolean;
}) {
  const translate = useTranslations("projects.tabs");
  return (
    <Tabs defaultValue={ProjectTab.GitHub} className="gap-6">
      <TabsList>
        <TabsTrigger value={ProjectTab.GitHub}>{translate("github")}</TabsTrigger>
        <TabsTrigger value={ProjectTab.Releases}>{translate("releases")}</TabsTrigger>
        <TabsTrigger value={ProjectTab.Dsn}>{translate("dsn")}</TabsTrigger>
        <TabsTrigger value={ProjectTab.Mcp}>{translate("mcp")}</TabsTrigger>
      </TabsList>
      <TabsContent value={ProjectTab.GitHub}>
        <Repositories projectId={projectId} projectName={projectName} canManage={canManage} />
      </TabsContent>
      <TabsContent value={ProjectTab.Releases}>
        <ReleasesTab projectId={projectId} canManage={canManage} />
      </TabsContent>
      <TabsContent value={ProjectTab.Dsn}>
        <DsnKeys projectId={projectId} publicId={publicId} />
      </TabsContent>
      <TabsContent value={ProjectTab.Mcp}>
        <McpTokens projectId={projectId} canManage={canManage} />
      </TabsContent>
    </Tabs>
  );
}

// The Releases tab: manual release recording plus the CI release tokens. Recording a release maps a
// version to a commit, so it needs GitHub connected — until then we prompt for that instead of an empty
// record form (a GitHub-less self-host falls through to the normal view). The release tokens sit below
// and always show, since CI mints one to record releases over the API regardless of the GitHub link.
function ReleasesTab({ projectId, canManage }: { projectId: number; canManage: boolean }) {
  const translate = useTranslations("projects");
  const github = useGithubConnection();
  const needsGithub = !github.loading && !github.connected && !github.notConfigured;
  return (
    <div className="flex flex-col gap-8">
      {needsGithub ? (
        <Notice>{translate("releasesNeedGithub")}</Notice>
      ) : (
        <Releases projectId={projectId} canManage={canManage} />
      )}
      <ReleaseTokens projectId={projectId} canManage={canManage} />
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
