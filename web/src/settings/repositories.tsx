"use client";

import { useQueryClient } from "@tanstack/react-query";
import { useTranslations } from "next-intl";
import { useState } from "react";
import {
  getListCodeMappingsQueryKey,
  getListReposQueryKey,
  useAddCodeMapping,
  useDeleteCodeMapping,
  useLinkRepo,
  useListCodeMappings,
  useListGithubBranches,
  useListGithubRepositories,
  useListRepos,
  useSuggestedMappings,
  useUnlinkRepo,
} from "@/src/api/generated/condux";
import type { CodeMapping, DerivedMapping, RepoLink } from "@/src/api/generated/model";
import { FIELD_CLASS, PRIMARY_BUTTON_CLASS, SECONDARY_BUTTON_CLASS } from "@/src/components/form";
import { Notice } from "@/src/components/notice";
import { Combobox } from "@/src/components/ui/combobox";
import { CveFindings } from "./cve-findings";
import { GitHubConnect } from "./github-connect";
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

// Disconnecting GitHub deliberately keeps repo links, because reconnecting restores them and unlinking
// would take a project's code mappings with it. The link is inert until then, so say so: otherwise the
// row looks normal and a fix run fails later with nothing on screen explaining why.
function RepoRow({
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

function CodeMappings({
  projectId,
  repoId,
  canManage,
}: {
  projectId: number;
  repoId: string;
  canManage: boolean;
}) {
  const translate = useTranslations("settings.repos");
  const queryClient = useQueryClient();
  const mappings = useListCodeMappings(projectId, repoId);
  const addMapping = useAddCodeMapping();
  const deleteMapping = useDeleteCodeMapping();
  // On-demand: only fetches when the user asks (the Suggest button calls refetch), since it hits GitHub.
  const suggestions = useSuggestedMappings(projectId, repoId, { query: { enabled: false } });
  const [stackRoot, setStackRoot] = useState("");
  const [sourceRoot, setSourceRoot] = useState("");

  const invalidate = () =>
    queryClient.invalidateQueries({ queryKey: getListCodeMappingsQueryKey(projectId, repoId) });

  const add = () => {
    const stack = stackRoot.trim();
    if (stack === "") {
      return;
    }
    addMapping.mutate(
      { projectId, repoId, data: { stackRoot: stack, sourceRoot: sourceRoot.trim() || null } },
      {
        onSuccess: () => {
          setStackRoot("");
          setSourceRoot("");
          invalidate();
        },
      },
    );
  };

  const remove = (mappingId: string) =>
    deleteMapping.mutate({ projectId, repoId, mappingId }, { onSuccess: invalidate });

  const addSuggestion = (suggestion: DerivedMapping) =>
    addMapping.mutate(
      {
        projectId,
        repoId,
        data: { stackRoot: suggestion.stackRoot, sourceRoot: suggestion.sourceRoot },
      },
      {
        onSuccess: () => {
          invalidate();
          suggestions.refetch(); // the added one drops off the suggestion list
        },
      },
    );

  const list: CodeMapping[] = mappings.data?.data ?? [];
  const suggested: DerivedMapping[] = suggestions.data?.data ?? [];
  return (
    <div className="mt-3 border-t border-border pt-3">
      <span className="text-xs uppercase text-muted-foreground">{translate("mappingsTitle")}</span>
      {list.length === 0 ? (
        <p className="mt-1 text-xs text-muted-foreground">{translate("mappingsEmpty")}</p>
      ) : (
        <ul className="mt-2 flex flex-col gap-1">
          {list.map((mapping) => (
            <li key={mapping.id} className="flex items-center justify-between gap-2 text-xs">
              <code className="text-foreground">
                {mapping.stackRoot} → {mapping.sourceRoot || translate("repoRoot")}
              </code>
              {canManage ? (
                <button
                  type="button"
                  onClick={() => remove(mapping.id)}
                  disabled={deleteMapping.isPending}
                  className="text-muted-foreground hover:text-error"
                >
                  {translate("delete")}
                </button>
              ) : null}
            </li>
          ))}
        </ul>
      )}
      {canManage ? (
        <>
          <div className="mt-2 flex flex-wrap items-center gap-2">
            <input
              aria-label={translate("stackRootLabel")}
              value={stackRoot}
              onChange={(event) => setStackRoot(event.target.value)}
              placeholder={translate("stackRootPlaceholder")}
              className={`${FIELD_CLASS} w-40 text-xs`}
            />
            <input
              aria-label={translate("sourceRootLabel")}
              value={sourceRoot}
              onChange={(event) => setSourceRoot(event.target.value)}
              placeholder={translate("sourceRootPlaceholder")}
              className={`${FIELD_CLASS} w-32 text-xs`}
            />
            <button
              type="button"
              onClick={add}
              disabled={addMapping.isPending}
              className={SECONDARY_BUTTON_CLASS}
            >
              {translate("addMapping")}
            </button>
            <button
              type="button"
              onClick={() => suggestions.refetch()}
              disabled={suggestions.isFetching}
              className="text-xs text-primary hover:underline disabled:opacity-50"
            >
              {suggestions.isFetching ? translate("suggesting") : translate("suggestMappings")}
            </button>
          </div>

          {suggestions.isFetched && suggested.length === 0 ? (
            <p className="mt-2 text-xs text-muted-foreground">{translate("suggestNone")}</p>
          ) : null}
          {suggested.length > 0 ? (
            <ul className="mt-2 flex flex-col gap-1">
              {suggested.map((suggestion) => (
                <li
                  key={`${suggestion.stackRoot}->${suggestion.sourceRoot}`}
                  className="flex items-center justify-between gap-2 text-xs"
                >
                  <code className="text-muted-foreground">
                    {suggestion.stackRoot} → {suggestion.sourceRoot || translate("repoRoot")}
                  </code>
                  <span className="flex items-center gap-2">
                    <span className="text-muted-foreground">
                      {translate("suggestMatches", { count: suggestion.matchCount })}
                    </span>
                    <button
                      type="button"
                      onClick={() => addSuggestion(suggestion)}
                      disabled={addMapping.isPending}
                      className="text-primary hover:underline"
                    >
                      {translate("add")}
                    </button>
                  </span>
                </li>
              ))}
            </ul>
          ) : null}
        </>
      ) : null}
    </div>
  );
}
