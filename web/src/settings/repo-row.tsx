"use client";

// One linked repository in a project's Repositories section: its name and branch, unlinking it, and the
// per-repo sections beneath it (CVE findings and code mappings).

import { useQueryClient } from "@tanstack/react-query";
import { useTranslations } from "next-intl";
import { useState } from "react";
import { getListReposQueryKey, useUnlinkRepo } from "@/src/api/generated/condux";
import type { RepoLink } from "@/src/api/generated/model";
import { SECONDARY_BUTTON_CLASS } from "@/src/components/form";
import { CodeMappings } from "./code-mappings";
import { CveFindings } from "./cve-findings";

// Disconnecting GitHub deliberately keeps repo links, because reconnecting restores them and unlinking
// would take a project's code mappings with it. The link is inert until then, so say so: otherwise the
// row looks normal and a fix run fails later with nothing on screen explaining why.
export function RepoRow({
  projectId,
  repo,
  canManage,
  inactive,
}: {
  projectId: number;
  repo: RepoLink;
  canManage: boolean;
  inactive: boolean;
}) {
  const translate = useTranslations("settings.repos");
  const queryClient = useQueryClient();
  const unlink = useUnlinkRepo();
  const [confirming, setConfirming] = useState(false);

  const remove = () =>
    unlink.mutate(
      { projectId, repoId: repo.id },
      {
        onSuccess: () =>
          queryClient.invalidateQueries({ queryKey: getListReposQueryKey(projectId) }),
      },
    );

  return (
    <li className="rounded-lg border border-border bg-card p-4">
      <div className="flex items-center justify-between gap-3">
        <div>
          <span className="text-sm font-medium text-foreground">{repo.repoFullName}</span>
          <span className="ml-2 text-xs text-muted-foreground">{repo.defaultBranch}</span>
          {inactive ? (
            <span className="ml-2 rounded border border-border px-1.5 py-0.5 text-xs text-muted-foreground">
              {translate("inactive")}
            </span>
          ) : null}
        </div>
        {canManage ? (
          confirming ? (
            <div className="flex items-center gap-2">
              <span className="text-xs text-muted-foreground">{translate("confirmUnlink")}</span>
              <button
                type="button"
                onClick={remove}
                disabled={unlink.isPending}
                className={SECONDARY_BUTTON_CLASS}
              >
                {translate("confirm")}
              </button>
              <button
                type="button"
                onClick={() => setConfirming(false)}
                className={SECONDARY_BUTTON_CLASS}
              >
                {translate("cancel")}
              </button>
            </div>
          ) : (
            <button
              type="button"
              onClick={() => setConfirming(true)}
              className={SECONDARY_BUTTON_CLASS}
            >
              {translate("unlink")}
            </button>
          )
        ) : null}
      </div>
      <CveFindings projectId={projectId} repoId={repo.id} canManage={canManage} />
      <CodeMappings projectId={projectId} repoId={repo.id} canManage={canManage} />
    </li>
  );
}
