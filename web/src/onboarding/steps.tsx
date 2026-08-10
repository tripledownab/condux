"use client";

import { useQueryClient } from "@tanstack/react-query";
import { useTranslations } from "next-intl";
import { useState } from "react";
import {
  getListReposQueryKey,
  useLinkRepo,
  useListIssues,
  useListKeys,
  useListRepos,
} from "@/src/api/generated/condux";
import { FIELD_CLASS, PRIMARY_BUTTON_CLASS } from "@/src/components/form";
import { buildDsn } from "@/src/lib/dsn";
import { GitHubConnect } from "@/src/settings/github-connect";
import { OnboardingStep, StepStatus } from "./onboarding-step";

// Step 3: connect GitHub to this project, then link a repo so the Conductor can propose fixes. Done once
// the project has a linked repo. GitHub connection lives here (per project, #134), not a separate org tab;
// GitHubConnect resolves the org from context, so no orgId is threaded through.
export function RepoStep({ projectId, projectName }: { projectId: number; projectName: string }) {
  const translate = useTranslations("onboarding.repo");
  const repos = useListRepos(projectId);
  const linked = repos.data?.data ?? [];
  const done = linked.length > 0;

  return (
    <OnboardingStep
      index={3}
      status={done ? StepStatus.Done : StepStatus.Active}
      title={translate("title")}
      description={
        done
          ? translate("linked", { repo: linked[0].repoFullName })
          : translate("description", { project: projectName })
      }
    >
      <div className="flex flex-col gap-3">
        <GitHubConnect projectName={projectName} />
        <ConnectRepoForm projectId={projectId} projectName={projectName} />
      </div>
    </OnboardingStep>
  );
}

function ConnectRepoForm({ projectId, projectName }: { projectId: number; projectName: string }) {
  const translate = useTranslations("onboarding.repo");
  const queryClient = useQueryClient();
  const linkRepo = useLinkRepo();
  const [repoFullName, setRepoFullName] = useState("");

  const submit = (event: React.FormEvent) => {
    event.preventDefault();
    if (repoFullName.trim() === "") {
      return;
    }
    linkRepo.mutate(
      { projectId, data: { repoFullName: repoFullName.trim(), defaultBranch: null } },
      {
        onSuccess: () => {
          setRepoFullName("");
          queryClient.invalidateQueries({ queryKey: getListReposQueryKey(projectId) });
        },
      },
    );
  };

  return (
    <div className="flex flex-col gap-1.5">
      <span className="text-xs text-muted-foreground">
        {translate("repoLabel", { project: projectName })}
      </span>
      <form onSubmit={submit} className="flex flex-wrap items-end gap-2">
        <input
          value={repoFullName}
          onChange={(event) => setRepoFullName(event.target.value)}
          placeholder={translate("placeholder")}
          aria-label={translate("repoLabel", { project: projectName })}
          className={FIELD_CLASS}
        />
        <button type="submit" disabled={linkRepo.isPending} className={PRIMARY_BUTTON_CLASS}>
          {linkRepo.isPending ? translate("connecting") : translate("connect")}
        </button>
      </form>
    </div>
  );
}

// Step 4: send the first event. Done once the project has an issue; polls while waiting so the step
// completes on its own when the first event lands. Shows the DSN to send with.
// projectId (the bigint) keys the issue/key hooks; publicId (the UUID) is the DSN's last segment (#126).
export function EventStep({ projectId, publicId }: { projectId: number; publicId: string }) {
  const translate = useTranslations("onboarding.event");
  // Issues are keyed by the numeric project id (a bigint FK to projects, #97), like the repo/key hooks.
  // Only "is there any issue" matters here, so ask for a single-row page and poll until it lands.
  const issues = useListIssues(
    projectId,
    { limit: 1 },
    {
      query: {
        // Poll until an issue lands; the response is a union (page | error), so narrow before reading.
        refetchInterval: (query) => {
          const data = query.state.data?.data;
          return data && "issues" in data && data.issues.length > 0 ? false : 5000;
        },
      },
    },
  );
  const keys = useListKeys(projectId);
  const listData = issues.data?.data;
  const done = !!listData && "issues" in listData && listData.issues.length > 0;
  const activeKey = (keys.data?.data ?? []).find((key) => key.isActive);

  return (
    <OnboardingStep
      index={4}
      status={done ? StepStatus.Done : StepStatus.Active}
      title={translate("title")}
      description={done ? translate("received") : translate("description")}
    >
      <div className="flex flex-col gap-2">
        {activeKey ? (
          <code className="block overflow-x-auto rounded bg-background p-2 text-xs text-muted-foreground">
            {buildDsn(activeKey.publicKey, publicId)}
          </code>
        ) : (
          <p className="text-xs text-muted-foreground">{translate("noKey")}</p>
        )}
        <p className="text-xs text-muted-foreground">{translate("waiting")}</p>
      </div>
    </OnboardingStep>
  );
}
