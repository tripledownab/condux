"use client";

import { useTranslations } from "next-intl";
import { useEffect, useState } from "react";
import { useGetLinearProject } from "@/src/api/generated/condux";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/src/components/ui/tabs";
import { readErrorStatus } from "@/src/lib/api-errors";
import { readSearchParam } from "@/src/lib/url-params";
import { LinearTab } from "@/src/linear/linear-tab";
import { DsnKeys } from "@/src/settings/dsn-keys";
import { McpTokens } from "@/src/settings/mcp-tokens";
import { ReleaseTokens } from "@/src/settings/release-tokens";
import { Releases } from "@/src/settings/releases";
import { Repositories } from "@/src/settings/repositories";
import { useGithubConnection } from "@/src/settings/use-github-connection";
import { ProjectTab } from "./project-tab";

// The management sections beneath a project's overview card, one per tab, so GitHub, releases, keys and
// Linear do not crowd a single scroll.

// The tab named by ?tab=, which is how a connect flow that left the app lands back on the tab it started
// from. Read once on mount, from window for the same reason as the connect panels.
function useInitialTab(): [ProjectTab, (tab: ProjectTab) => void] {
  const [tab, setTab] = useState(ProjectTab.GitHub);
  useEffect(() => {
    const named = readSearchParam("tab");
    if (Object.values(ProjectTab).includes(named as ProjectTab)) {
      setTab(named as ProjectTab);
    }
  }, []);
  return [tab, setTab];
}

// The project's management surface. Each panel's data loads only while its tab is mounted (Radix unmounts
// the inactive panels), so opening the page does not fire every section's queries at once. The Linear tab
// is the one exception: its status is read up front, since a 404 means Linear is not configured on this
// server and the tab should not exist at all.
export function ProjectSections({
  projectId,
  publicId,
  projectName,
  orgId,
  canManage,
}: {
  projectId: number;
  publicId: string;
  projectName: string;
  orgId: number | null;
  canManage: boolean;
}) {
  const translate = useTranslations("projects.tabs");
  const [tab, setTab] = useInitialTab();
  const linear = useGetLinearProject(projectId);
  const linearAvailable =
    orgId !== null && !linear.isPending && readErrorStatus(linear.error) !== 404;

  return (
    <Tabs value={tab} onValueChange={(value) => setTab(value as ProjectTab)} className="gap-6">
      <TabsList>
        <TabsTrigger value={ProjectTab.GitHub}>{translate("github")}</TabsTrigger>
        <TabsTrigger value={ProjectTab.Releases}>{translate("releases")}</TabsTrigger>
        <TabsTrigger value={ProjectTab.Dsn}>{translate("dsn")}</TabsTrigger>
        <TabsTrigger value={ProjectTab.Mcp}>{translate("mcp")}</TabsTrigger>
        {linearAvailable ? (
          <TabsTrigger value={ProjectTab.Linear}>{translate("linear")}</TabsTrigger>
        ) : null}
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
      {linearAvailable && orgId !== null ? (
        <TabsContent value={ProjectTab.Linear}>
          <LinearTab projectId={projectId} orgId={orgId} canManage={canManage} />
        </TabsContent>
      ) : null}
    </Tabs>
  );
}

// The Releases tab: manual release recording plus the CI release tokens. Recording a release maps a
// version to a commit, so it needs a linked repository, and linking one needs GitHub connected when the
// GitHub App is configured; only the record form says so. The section and the tokens always show, since a
// token also uploads source maps and debug files, which need neither.
function ReleasesTab({ projectId, canManage }: { projectId: number; canManage: boolean }) {
  const github = useGithubConnection();
  const needsGithub = !github.loading && !github.connected && !github.notConfigured;
  return (
    <div className="flex flex-col gap-8">
      <Releases projectId={projectId} canManage={canManage} needsGithub={needsGithub} />
      <ReleaseTokens projectId={projectId} canManage={canManage} />
    </div>
  );
}
