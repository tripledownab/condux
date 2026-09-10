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
import { fixErrorKey } from "./fix-format";
import { SuggestFixDialog } from "./suggest-fix-dialog";

// The Conductor trigger on an issue. Once a fix has been requested for this issue the button becomes
// "View suggested fix" pointing straight at that run in the Fixes section — so the same issue can't be
// re-requested, and the run's progress + draft PR are watched there, not in this rail.
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
  const fixes = useListFixes(projectId, issueId, {
    query: {
      // The run is created asynchronously by the worker; after a request, poll briefly so the link
      // resolves from the generic Fixes section to the specific new run once its row appears.
      refetchInterval: (query) =>
        requestFix.isSuccess && (query.state.data?.data ?? []).length === 0 ? 3000 : false,
    },
  });
  const latestFix = fixes.data?.data?.[0];

  const suggest = (repoId: string | null, baseBranch: string | null) =>
    requestFix.mutate(
      { projectId, issueId, data: { repoId, baseBranch } },
      {
        onSuccess: () =>
          queryClient.invalidateQueries({ queryKey: getListFixesQueryKey(projectId, issueId) }),
        onError: (error) => {
          if (fixErrorKey(error) === "noRepo") {
            setShowConnectRepo(true);
          }
        },
      },
    );

  // A fix already exists (or one was just requested): funnel to it instead of offering another.
  if (latestFix !== undefined || requestFix.isSuccess) {
    return (
      <section>
        <Button asChild className="w-full">
          <Link href={latestFix ? fixDetailPath(latestFix.id) : ROUTES.fixes}>
            {translate("view")}
          </Link>
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
