"use client";

import { usePathname } from "next/navigation";
import { useTranslations } from "next-intl";
import { useEffect, useState } from "react";
import type { ConduxApiError } from "@/src/api/fetcher";
import { useDisconnectGithub, useGithubConnect, useGithubHealth } from "@/src/api/generated/condux";
import type { GithubHealthResponse } from "@/src/api/generated/model";
import { PRIMARY_BUTTON_CLASS, SECONDARY_BUTTON_CLASS } from "@/src/components/form";
import { GitHubInstallationPicker } from "./github-installation-picker";
import { useGithubConnection } from "./use-github-connection";

type Translate = ReturnType<typeof useTranslations>;

// What the live check found. The stored link and the real connection can disagree in both directions:
// a link lost locally still leaves the installation on GitHub, and an app uninstalled on GitHub leaves
// our row behind. Each state gets its own message because the remedy differs (reconnect, or retry).
const HEALTH_MESSAGE_KEYS: Record<string, string> = {
  healthy: "healthHealthy",
  not_connected: "healthNotConnected",
  revoked: "healthRevoked",
  unreachable: "healthUnreachable",
};

const HEALTHY = "healthy";

// The connected sentence. A project may or may not be in scope, and GitHub only names the account once it
// tells us, so each case has its own wording — a placeholder standing in for an account we don't have
// ("connected as your GitHub account") reads like a bug and says nothing.
function connectedMessage(
  translate: Translate,
  account: string | null | undefined,
  projectName: string | undefined,
): string {
  if (projectName) {
    return account
      ? translate("connectedFor", { account, project: projectName })
      : translate("connectedForUnnamed", { project: projectName });
  }
  return account ? translate("connected", { account }) : translate("connectedUnnamed");
}

// The check's verdict, carrying whether it passed so the caller styles it without re-reading the status.
type HealthMessage = { text: string; healthy: boolean };

function healthMessage(
  translate: Translate,
  health: GithubHealthResponse | undefined,
): HealthMessage | null {
  if (!health) {
    return null;
  }

  if (health.status === HEALTHY) {
    return {
      healthy: true,
      text: health.accountLogin
        ? translate("healthHealthy", { account: health.accountLogin })
        : translate("healthHealthyUnnamed"),
    };
  }

  // A status we have no wording for means the API moved; say nothing rather than guess a remedy.
  const key = HEALTH_MESSAGE_KEYS[health.status];
  return key ? { healthy: false, text: translate(key) } : null;
}

// How a connect attempt came back. The flow leaves the app and returns through GitHub, so the outcome
// arrives as a query param rather than as a response to anything we called.
enum ConnectOutcome {
  Connected = "connected",
  Select = "select",
  Taken = "taken",
  // Installed on GitHub, but held for an organization owner to approve, so there is nothing to link yet.
  Pending = "pending",
  Failed = "failed",
}

// What a returned outcome says, and whether it reads as a failure. Pending is not one: the app is
// installed and a GitHub organization owner has to approve it, so styling it as an error would report a
// problem that is not there. Each entry states its own tone rather than inheriting one from the slot it
// renders in, so a new outcome has to decide.
const OUTCOME_MESSAGES: Partial<Record<ConnectOutcome, { key: string; failed: boolean }>> = {
  [ConnectOutcome.Taken]: { key: "selectTaken", failed: true },
  [ConnectOutcome.Pending]: { key: "pending", failed: false },
  [ConnectOutcome.Failed]: { key: "failed", failed: true },
};

type ReturnedOutcome = { status: ConnectOutcome; selection: string | null };

// Read from window rather than useSearchParams, which would force a Suspense boundary on every page that
// renders this panel (same reason as the auth pages).
function readOutcome(): ReturnedOutcome | null {
  const params = new URLSearchParams(window.location.search);
  const status = params.get("github");
  return Object.values(ConnectOutcome).includes(status as ConnectOutcome)
    ? { status: status as ConnectOutcome, selection: params.get("selection") }
    : null;
}

// Drop the flow's params once they have been acted on, so a reload doesn't replay a finished outcome.
function clearOutcome() {
  const url = new URL(window.location.href);
  url.searchParams.delete("github");
  url.searchParams.delete("selection");
  window.history.replaceState(null, "", url.toString());
}

// The GitHub connection, surfaced inside a project (#134 — connection is per-project, not a separate org
// tab). The GitHub App installs once per org (GitHub's model), so the first project to connect installs it
// for the org and every other project reuses it — no re-prompt. Starting the flow returns the browser to
// the current page (a signed returnPath in the connect state). Reused by the project Repositories section
// and the onboarding connect-repo step. The org + role come from context (useCurrentOrg) — single org per
// user, so it is the viewed project's org — rather than being drilled through as props. Admin+ connects;
// the endpoints 404 when the App isn't configured.
export function GitHubConnect({ projectName }: { projectName?: string }) {
  const translate = useTranslations("settings.github");
  const pathname = usePathname();
  const { orgId, loading, notConfigured, connected, canManage, installation, refetch } =
    useGithubConnection();
  const connect = useGithubConnect<ConduxApiError>();
  // On demand only: a check spends a real GitHub call, so it must not fire on every page view.
  const [checking, setChecking] = useState(false);
  const health = useGithubHealth(orgId, { query: { enabled: checking } });
  const [outcome, setOutcome] = useState<ReturnedOutcome | null>(null);
  useEffect(() => setOutcome(readOutcome()), []);
  // Disconnecting is reversible but not obvious in its consequences (the app stays on GitHub), so it
  // confirms rather than firing on the first click.
  const [confirmingDisconnect, setConfirmingDisconnect] = useState(false);
  const disconnect = useDisconnectGithub();

  // Feature off on this server (both routes 404 behind the same opt-in config): show nothing.
  if (loading || notConfigured || connect.error?.status === 404) {
    return null;
  }

  const start = () =>
    connect.mutate(
      { orgId, data: { returnPath: pathname } },
      {
        onSuccess: (response) => {
          if (response.status === 200) {
            window.location.href = response.data.installUrl;
          }
        },
      },
    );

  // Several installations were reachable, so the user has to say which account this org connects to.
  if (outcome?.status === ConnectOutcome.Select && outcome.selection && canManage) {
    return (
      <div className="rounded-lg border border-border bg-card p-4">
        <GitHubInstallationPicker
          orgId={orgId}
          selection={outcome.selection}
          onSelected={() => {
            clearOutcome();
            setOutcome(null);
            refetch();
          }}
        />
      </div>
    );
  }

  const outcomeMessage = outcome ? OUTCOME_MESSAGES[outcome.status] : undefined;
  const connectedText = connectedMessage(translate, installation?.accountLogin, projectName);
  const connectPromptText = projectName
    ? translate("connectPromptFor", { project: projectName })
    : translate("connectPrompt");
  const connectFailed = connect.isError && connect.error?.status !== 404;

  // The check is offered in both states on purpose: a stored link can be dead on GitHub, and a missing
  // one can still have a live installation behind it, so neither state is trustworthy on its own.
  const verdict = healthMessage(
    translate,
    health.data?.status === 200 ? health.data.data : undefined,
  );
  // Each action is independently conditional, so they are built separately and the row below renders
  // whichever apply. Keeping the verdict and error text out of them is what lets the actions share a row.
  const checkConnectionButton = (
    <button
      type="button"
      onClick={() => {
        setChecking(true);
        health.refetch();
      }}
      disabled={health.isFetching}
      className={SECONDARY_BUTTON_CLASS}
    >
      {health.isFetching ? translate("checking") : translate("checkConnection")}
    </button>
  );

  const connectButton =
    connected || !canManage ? null : (
      <button
        type="button"
        onClick={start}
        disabled={connect.isPending}
        className={PRIMARY_BUTTON_CLASS}
      >
        {connect.isPending ? translate("connecting") : translate("connect")}
      </button>
    );

  // Changing which repositories Condux can see, and uninstalling, both live on GitHub. Linking straight to
  // the installation saves hunting through GitHub's settings, and only means anything once one exists.
  // inline-flex gives the anchor the same box as the buttons beside it, which it would not have inline.
  // Only offered while something is linked, and only to someone who could reconnect it.
  const disconnectButton =
    !installation || !canManage ? null : (
      <button
        type="button"
        onClick={() => setConfirmingDisconnect(true)}
        disabled={disconnect.isPending}
        className={SECONDARY_BUTTON_CLASS}
      >
        {disconnect.isPending ? translate("disconnecting") : translate("disconnect")}
      </button>
    );

  const manageLink = installation ? (
    <a
      href={installation.manageUrl}
      target="_blank"
      rel="noopener noreferrer"
      className={`inline-flex items-center ${SECONDARY_BUTTON_CLASS}`}
    >
      {translate("manageOnGithub")}
    </a>
  ) : null;

  return (
    <div className="rounded-lg border border-border bg-card p-4">
      <div className="flex flex-col items-start gap-3">
        <p className="text-sm text-muted-foreground">
          {connected ? connectedText : connectPromptText}
        </p>
        {/* Every action on one row. items-center keeps them aligned despite the primary and secondary
            button styles having different vertical padding. */}
        <div className="flex flex-wrap items-center gap-2">
          {connectButton}
          {checkConnectionButton}
          {manageLink}
          {disconnectButton}
        </div>
        {confirmingDisconnect ? (
          <div className="flex flex-col items-start gap-2 rounded-md border border-border bg-background p-3">
            <p className="text-sm text-muted-foreground">{translate("disconnectConfirm")}</p>
            <div className="flex flex-wrap items-center gap-2">
              <button
                type="button"
                onClick={() =>
                  disconnect.mutate(
                    { orgId },
                    {
                      onSuccess: () => {
                        setConfirmingDisconnect(false);
                        refetch();
                      },
                    },
                  )
                }
                disabled={disconnect.isPending}
                className={PRIMARY_BUTTON_CLASS}
              >
                {disconnect.isPending ? translate("disconnecting") : translate("disconnect")}
              </button>
              <button
                type="button"
                onClick={() => setConfirmingDisconnect(false)}
                className={SECONDARY_BUTTON_CLASS}
              >
                {translate("cancel")}
              </button>
            </div>
            {disconnect.isError ? (
              <p className="text-sm text-error">{translate("disconnectFailed")}</p>
            ) : null}
          </div>
        ) : null}
        {!connected && !canManage ? (
          <p className="text-xs text-muted-foreground">{translate("adminOnly")}</p>
        ) : null}
        {connectFailed ? <p className="text-sm text-error">{translate("failed")}</p> : null}
        {verdict ? (
          <p className={verdict.healthy ? "text-sm text-muted-foreground" : "text-sm text-error"}>
            {verdict.text}
          </p>
        ) : null}
        {outcomeMessage ? (
          <p
            className={
              outcomeMessage.failed ? "text-sm text-error" : "text-sm text-muted-foreground"
            }
          >
            {translate(outcomeMessage.key)}
          </p>
        ) : null}
      </div>
    </div>
  );
}
