"use client";

import { useQueryClient } from "@tanstack/react-query";
import { useTranslations } from "next-intl";
import { useState } from "react";
import {
  getListReposQueryKey,
  useLinkRepo,
  useListGithubBranches,
  useListGithubRepositories,
  useListRepos,
} from "@/src/api/generated/condux";
import { FIELD_CLASS, PRIMARY_BUTTON_CLASS } from "@/src/components/form";
import { Notice } from "@/src/components/notice";
import { Combobox } from "@/src/components/ui/combobox";
import { GitHubConnect } from "./github-connect";
import { RepoRow } from "./repo-row";
import { useGithubConnection } from "./use-github-connection";

// owner/name split, tolerating a name with no slash rather than producing a silently wrong prefix.
const repoOwner = (fullName: string) => {
  const slash = fullName.indexOf("/");
  return slash === -1 ? "" : fullName.slice(0, slash);
};

const repoShortName = (fullName: string) => {
  const slash = fullName.indexOf("/");
  return slash === -1 ? fullName : fullName.slice(slash + 1);
};

// An installation is scoped to one account, so every repo it offers carries the same owner prefix, which
// costs the width the distinguishing part needs (a long org leaves only a few characters of the name
// visible). Drop it for display, but only when they really do all share an owner, and keep the value as
// the full owner/name that gets linked.
function repoPickerOptions(fullNames: readonly string[]) {
  const sharesOneOwner =
    fullNames.length > 0 && fullNames.every((name) => repoOwner(name) === repoOwner(fullNames[0]));
  return (
    fullNames
      .map((name) => ({
        value: name,
        label: sharesOneOwner ? repoShortName(name) : name,
      }))
      // GitHub returns them in its own order, which reads as unsorted. Sort on the label, since that is
      // what someone scans, and case-insensitively so a capitalised repo does not sort into its own block.
      .sort((a, b) => a.label.localeCompare(b.label, undefined, { sensitivity: "base" }))
  );
}

// The repositories a project's fixes act on (#89/#114): connect GitHub (#134), link a GitHub repo by
// owner/name, set the code mappings that turn a stack-frame path into a repo path, and unlink. Reads are
// member+, mutations admin+ (the API enforces it); non-admins see a read-only view.
export function Repositories({
  projectId,
  projectName,
  canManage,
}: {
  projectId: number;
  projectName: string;
  canManage: boolean;
}) {
  const translate = useTranslations("settings.repos");
  const tCommon = useTranslations("common");
  const queryClient = useQueryClient();
  const repos = useListRepos(projectId);
  const linkRepo = useLinkRepo();
  const github = useGithubConnection();
  const [fullName, setFullName] = useState("");
  const [branch, setBranch] = useState("");

  // What the installation can actually reach, so a repo is picked rather than typed. Typing invites a
  // name that does not exist or sits outside the installation, which links fine and only fails later
  // when a fix run cannot mint a token for it.
  const reachable = useListGithubRepositories(github.orgId, {
    query: { enabled: github.connected },
  });
  // The branch list is keyed on the chosen repo, so it is only meaningful once one is chosen.
  const branches = useListGithubBranches(
    github.orgId,
    { repo: fullName },
    { query: { enabled: github.connected && fullName !== "" } },
  );

  const link = () => {
    const repoFullName = fullName.trim();
    if (repoFullName === "") {
      return;
    }
    linkRepo.mutate(
      { projectId, data: { repoFullName, defaultBranch: branch.trim() || null } },
      {
        onSuccess: () => {
          setFullName("");
          setBranch("");
          queryClient.invalidateQueries({ queryKey: getListReposQueryKey(projectId) });
        },
      },
    );
  };

  const list = repos.data?.data ?? [];
  // A repo may only be linked once GitHub is connected (the Conductor needs an installation token to
  // reach it — otherwise the link is dead). Until then the GitHubConnect panel above is the CTA. A
  // server with the GitHub App off (notConfigured, e.g. a self-host) still links for source deep-links.
  const canLink = canManage && (github.connected || github.notConfigured);
  // Linking the same repo twice says nothing new, so only offer what is not already linked.
  const linkedNames = new Set(list.map((repo) => repo.repoFullName));
  const repoOptions = repoPickerOptions(
    (reachable.data?.status === 200 ? reachable.data.data.repositories : []).filter(
      (name) => !linkedNames.has(name),
    ),
  );
  const branchOptions = (branches.data?.status === 200 ? branches.data.data.branches : []).map(
    (name) => ({ value: name, label: name }),
  );
  // With the App configured the pickers are the whole story. A server without it (a self-host wiring up
  // source deep-links only) has no installation to ask, so it keeps typing them.
  const picksFromGithub = github.connected;
  return (
    <section>
      <h2 className="font-heading text-lg font-semibold text-foreground">{translate("title")}</h2>
      <p className="mt-1 text-sm text-muted-foreground">{translate("description")}</p>

      <div className="mt-3">
        <GitHubConnect projectName={projectName} />
      </div>

      {canLink ? (
        <div className="mt-3 flex flex-wrap items-center gap-2">
          {picksFromGithub ? (
            <div className="w-56">
              <Combobox
                value={fullName}
                onValueChange={(name) => {
                  setFullName(name);
                  // The branches belong to the previous repo, so keep nothing from it.
                  setBranch("");
                }}
                options={repoOptions}
                aria-label={translate("repoLabel")}
                placeholder={translate("repoSelect")}
                searchPlaceholder={tCommon("comboboxSearch")}
                emptyText={tCommon("comboboxEmpty")}
              />
            </div>
          ) : (
            <input
              aria-label={translate("repoLabel")}
              value={fullName}
              onChange={(event) => setFullName(event.target.value)}
              placeholder={translate("repoPlaceholder")}
              className={`${FIELD_CLASS} w-56`}
            />
          )}
          {picksFromGithub ? (
            <div className="w-40">
              <Combobox
                value={branch}
                onValueChange={setBranch}
                options={branchOptions}
                // Nothing to choose from until a repo is picked, and the server defaults the branch when
                // none is sent, so an empty selection stays valid.
                disabled={branchOptions.length === 0}
                aria-label={translate("branchLabel")}
                placeholder={translate("branchSelect")}
                searchPlaceholder={tCommon("comboboxSearch")}
                emptyText={tCommon("comboboxEmpty")}
              />
            </div>
          ) : (
            <input
              aria-label={translate("branchLabel")}
              value={branch}
              onChange={(event) => setBranch(event.target.value)}
              placeholder={translate("branchPlaceholder")}
              className={`${FIELD_CLASS} w-40`}
            />
          )}
          <button
            type="button"
            onClick={link}
            disabled={linkRepo.isPending}
            className={PRIMARY_BUTTON_CLASS}
          >
            {linkRepo.isPending ? translate("linking") : translate("link")}
          </button>
        </div>
      ) : null}

      <div className="mt-4">
        {repos.isPending ? (
          <Notice>{translate("loading")}</Notice>
        ) : repos.isError ? (
          <Notice>{translate("error")}</Notice>
        ) : list.length === 0 ? (
          <Notice>{translate("empty")}</Notice>
        ) : (
          <ul className="flex flex-col gap-3">
            {list.map((repo) => (
              <RepoRow
                key={repo.id}
                projectId={projectId}
                repo={repo}
                canManage={canManage}
                inactive={github.needsReconnect}
              />
            ))}
          </ul>
        )}
      </div>
    </section>
  );
}
