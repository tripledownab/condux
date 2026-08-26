"use client";

import { useQueryClient } from "@tanstack/react-query";
import { useFormatter, useTranslations } from "next-intl";
import { type FormEvent, useState } from "react";
import {
  getListReleasesQueryKey,
  useListReleases,
  useListRepos,
  useRecordRelease,
} from "@/src/api/generated/condux";
import type { RepoLink } from "@/src/api/generated/model";
import { FIELD_CLASS, PRIMARY_BUTTON_CLASS } from "@/src/components/form";
import { Combobox } from "@/src/components/ui/combobox";

// Releases (#89/#144): map a deployed version to its commit, so the Conductor can fix against the exact
// ref and issues can show the release they first appeared in. A release is normally POSTed from CI on
// deploy; this is the manual dashboard path. Recording is admin+; viewing is member+ (the API enforces it).
export function Releases({ projectId, canManage }: { projectId: number; canManage: boolean }) {
  const translate = useTranslations("settings.releases");
  const format = useFormatter();
  const releases = useListReleases(projectId);
  const repos = useListRepos(projectId);
  const list = releases.data?.data ?? [];
  const linked = repos.data?.data ?? [];
  const repoName = (id: string) => linked.find((repo) => repo.id === id)?.repoFullName ?? id;

  return (
    <section>
      <h2 className="font-heading text-lg font-semibold text-foreground">{translate("title")}</h2>
      <p className="mt-1 text-sm text-muted-foreground">{translate("description")}</p>

      {canManage ? <RecordForm projectId={projectId} repos={linked} /> : null}

      <div className="mt-4">
        {list.length === 0 ? (
          <p className="text-xs text-muted-foreground">{translate("empty")}</p>
        ) : (
          <ul className="flex flex-col gap-2">
            {list.map((release) => (
              <li
                key={release.id}
                className="flex flex-wrap items-center justify-between gap-3 rounded-lg border border-border bg-card p-3 text-sm"
              >
                <span className="font-medium text-foreground">{release.version}</span>
                <div className="flex items-center gap-3 text-xs text-muted-foreground">
                  <span className="font-mono">{release.commitSha.slice(0, 8)}</span>
                  <span className="truncate">{repoName(release.repoLinkId)}</span>
                  <span>
                    {format.dateTime(new Date(release.createdAt), {
                      month: "short",
                      day: "numeric",
                    })}
                  </span>
                </div>
              </li>
            ))}
          </ul>
        )}
      </div>
    </section>
  );
}

function RecordForm({ projectId, repos }: { projectId: number; repos: RepoLink[] }) {
  const translate = useTranslations("settings.releases");
  const tCommon = useTranslations("common");
  const queryClient = useQueryClient();
  const record = useRecordRelease();
  const [version, setVersion] = useState("");
  const [commitSha, setCommitSha] = useState("");
  const [repoLinkId, setRepoLinkId] = useState("");

  if (repos.length === 0) {
    return <p className="mt-3 text-xs text-muted-foreground">{translate("needRepo")}</p>;
  }

  // Default to the sole/first linked repo until the user picks another.
  const selectedRepo = repoLinkId || repos[0].id;
  const repoOptions = repos.map((repo) => ({ value: repo.id, label: repo.repoFullName }));

  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (version.trim() === "" || commitSha.trim() === "") {
      return;
    }
    record.mutate(
      {
        projectId,
        data: { repoLinkId: selectedRepo, version: version.trim(), commitSha: commitSha.trim() },
      },
      {
        onSuccess: () => {
          setVersion("");
          setCommitSha("");
          queryClient.invalidateQueries({ queryKey: getListReleasesQueryKey(projectId) });
        },
      },
    );
  };

  return (
    <form onSubmit={submit} className="mt-3 flex flex-wrap items-end gap-2">
      <input
        value={version}
        onChange={(event) => setVersion(event.target.value)}
        placeholder={translate("versionPlaceholder")}
        aria-label={translate("version")}
        className={FIELD_CLASS}
      />
      <input
        value={commitSha}
        onChange={(event) => setCommitSha(event.target.value)}
        placeholder={translate("commitPlaceholder")}
        aria-label={translate("commit")}
        className={`${FIELD_CLASS} font-mono`}
      />
      <Combobox
        value={selectedRepo}
        onValueChange={setRepoLinkId}
        options={repoOptions}
        aria-label={translate("repo")}
        className="w-56"
        searchPlaceholder={tCommon("comboboxSearch")}
        emptyText={tCommon("comboboxEmpty")}
      />
      <button type="submit" disabled={record.isPending} className={PRIMARY_BUTTON_CLASS}>
        {record.isPending ? translate("recording") : translate("record")}
      </button>
      {record.isError ? (
        <p className="w-full text-sm text-error">{translate("recordError")}</p>
      ) : null}
    </form>
  );
}
