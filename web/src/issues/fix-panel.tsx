"use client";

import { useQueryClient } from "@tanstack/react-query";
import Link from "next/link";
import { useTranslations } from "next-intl";
import { useState } from "react";
import { getListFixesQueryKey, useListFixes, useRequestFix } from "@/src/api/generated/condux";
import { PRIMARY_BUTTON_CLASS } from "@/src/components/form";
import { Button } from "@/src/components/ui/button";
import { fixDetailPath, ROUTES } from "@/src/routes";
import { useGithubConnection } from "@/src/settings/use-github-connection";
import { ConnectRepoDialog } from "./connect-repo-dialog";
import { canRequestAnotherFix, fixErrorKey, isAwaitingNewRun } from "./fix-format";
import { SuggestFixDialog } from "./suggest-fix-dialog";

// The Conductor trigger on an issue. Once a fix has been requested for this issue the button becomes
// "View suggested fix" pointing straight at that run in the Fixes section, where its progress and draft
// PR are watched. A run that ended without a PR (failed or cancelled) offers the button again beside a
// link to that attempt, since its allowance was refunded and its failure notice says to retry here.
export function FixPanel({
  projectId,
  issueId,
  orgId,
  lastSeen,
}: {
  projectId: number;
  issueId: string;
  orgId: number;
  lastSeen?: string;
}) {
  const translate = useTranslations("issues.fix");
  const queryClient = useQueryClient();
  const requestFix = useRequestFix();
  // A project with no connected repo can't get a fix (the Conductor opens draft PRs), so that rejection
  // opens a modal pointing to GitHub rather than a dead-end inline error.
  const [showConnectRepo, setShowConnectRepo] = useState(false);
  // A run costs a real allowance, so it is confirmed first (#128) — the button opens this dialog, and
  // only Confirm fires the request.
  const [showConfirm, setShowConfirm] = useState(false);
  // A run needs an installation token, so an org whose GitHub connection is gone cannot start one. The
  // repo link survives a disconnect on purpose, which is exactly why the button has to say this: without
  // it the request goes out and dies in the worker with nothing on screen.
  const github = useGithubConnection();
  // The run a retry replaces, so the link waits for the new run rather than showing the old one.
  const [replacedFixId, setReplacedFixId] = useState<string | null>(null);
  // The run the outstanding request replaces; undefined while no request has succeeded.
  const awaitingReplaced = requestFix.isSuccess ? replacedFixId : undefined;
  const fixes = useListFixes(projectId, issueId, {
    query: {
      // After a request, poll until the list shows its run, so the link resolves from the generic Fixes
      // section to the specific new run.
      refetchInterval: (query) =>
        isAwaitingNewRun(awaitingReplaced, query.state.data?.data?.[0]?.id) ? 3000 : false,
    },
  });
  const latestFix = fixes.data?.data?.[0];
  // After a retry, only the NEW run can offer another: the one it replaced is still the latest until
  // the new row appears, and offering a second request over it would ask twice for the same thing.
  const canRetry =
    latestFix !== undefined &&
    canRequestAnotherFix(latestFix.status) &&
    !isAwaitingNewRun(awaitingReplaced, latestFix.id);

  const suggest = (repoId: string | null, baseBranch: string | null) => {
    setReplacedFixId(latestFix?.id ?? null);
    requestFix.mutate(
      { projectId, issueId, data: { repoId, baseBranch } },
      {
        onSuccess: () =>
          queryClient.invalidateQueries({ queryKey: getListFixesQueryKey(projectId, issueId) }),
        onError: (error) => {
          const key = fixErrorKey(error);
          if (key === "noRepo") {
            setShowConnectRepo(true);
          }
          // Someone else's run is queued or running: read the list again so the panel shows it.
          if (key === "inProgress") {
            queryClient.invalidateQueries({ queryKey: getListFixesQueryKey(projectId, issueId) });
          }
        },
      },
    );
  };

  // A fix already exists (or one was just requested): funnel to it instead of offering another, unless
  // it ended without a PR. After a retry the link waits for the new run, so it never points at the old.
  if ((latestFix !== undefined || requestFix.isSuccess) && !canRetry) {
    const shown =
      latestFix !== undefined && !isAwaitingNewRun(awaitingReplaced, latestFix.id)
        ? latestFix
        : undefined;
    return (
      <section>
        <Button asChild className="w-full">
          <Link href={shown ? fixDetailPath(shown.id) : ROUTES.fixes}>{translate("view")}</Link>
        </Button>
      </section>
    );
  }

  // The expected control-plane rejections (wrong role, plan/quota) get a specific line; the no-repo
  // case is handled by the modal instead of a dead-end inline message.
  const errorKey = requestFix.isError ? fixErrorKey(requestFix.error) : null;
  const inlineError = errorKey !== null && errorKey !== "noRepo" ? errorKey : null;

  return (
    <section>
      <button
        type="button"
        onClick={() => setShowConfirm(true)}
        disabled={requestFix.isPending || github.needsReconnect}
        className={`w-full ${PRIMARY_BUTTON_CLASS}`}
      >
        {requestFix.isPending ? translate("suggesting") : translate("suggest")}
      </button>
      {canRetry ? (
        <p className="mt-3 text-sm text-muted-foreground">
          {translate("lastAttemptFailed")}{" "}
          <Link href={fixDetailPath(latestFix.id)} className="underline underline-offset-4">
            {translate("viewLastAttempt")}
          </Link>
        </p>
      ) : null}
      {github.needsReconnect ? (
        <p className="mt-3 text-sm text-muted-foreground">
          {translate("githubDisconnected")}{" "}
          <Link href={ROUTES.projects} className="underline underline-offset-4">
            {translate("reconnectGithub")}
          </Link>
        </p>
      ) : null}
      {inlineError ? (
        <p className="mt-3 text-sm text-error">
          {translate(inlineError)}
          {/* A spent allowance and a reached compute ceiling are both recoverable by the user, so they
              get a way out rather than just a statement of the problem. Neither is retryable in place,
              which is exactly why they must not fall through to the generic "try again". */}
          {inlineError === "quotaExceeded" || inlineError === "costCapExceeded" ? (
            <>
              {" "}
              <Link href={ROUTES.settingsGeneral} className="underline underline-offset-4">
                {translate("seePlans")}
              </Link>
            </>
          ) : null}
        </p>
      ) : null}
      <SuggestFixDialog
        open={showConfirm}
        onOpenChange={setShowConfirm}
        orgId={orgId}
        projectId={projectId}
        lastSeen={lastSeen}
        pending={requestFix.isPending}
        onConfirm={(repoId, baseBranch) => {
          setShowConfirm(false);
          suggest(repoId, baseBranch);
        }}
      />
      <ConnectRepoDialog open={showConnectRepo} onOpenChange={setShowConnectRepo} />
    </section>
  );
}
