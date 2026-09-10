"use client";

import type { ConduxApiError } from "@/src/api/fetcher";
import { useListGithubInstallations } from "@/src/api/generated/condux";
import type { GithubInstallationResponse } from "@/src/api/generated/model";
import { OrgStatus, useCurrentOrg } from "@/src/orgs/current-org";

// The GitHub connection state for a project's org, shared by the connect panel (github-connect.tsx) and
// the Repositories section, which gates repo-linking on it — a repo can't be linked before GitHub is
// connected, since the Conductor would have no installation token to reach it (a dead link). Single org
// per user, so the current org is the viewed project's org. The installations query is cached, so both
// callers reuse one fetch. `notConfigured` means the GitHub App is off on this server (the routes 404) —
// a self-host without the App, where repo-linking is still allowed (source deep-links only, no Conductor).
export type GithubConnectionState = {
  orgId: number;
  loading: boolean;
  notConfigured: boolean;
  connected: boolean;
  canManage: boolean;
  // The linked installation, or undefined when the org has none. Its accountLogin is separately nullable
  // (GitHub names the account on the webhook, or on the connect callback), so "not connected" and "we
  // don't know which account" stay distinguishable instead of collapsing into one empty value. manageUrl
  // is where GitHub lets the user change repository access or uninstall.
  installation: GithubInstallationResponse | undefined;
  // The server runs the GitHub App but this org has no installation, so anything needing an installation
  // token is inert: linked repos cannot be reached and a fix run cannot start. Distinct from both
  // `connected` and `notConfigured`, because a self-host without the App is not in a broken state, it is
  // in a smaller one. Derived here so the repo list and the fix trigger cannot disagree about it.
  needsReconnect: boolean;
  // For the connect flow, which links an installation server-side and needs the panel to catch up.
  refetch: () => void;
};

export function useGithubConnection(): GithubConnectionState {
  const current = useCurrentOrg();
  const orgReady = current.status === OrgStatus.Ready;
  const orgId = orgReady ? current.org.id : 0;

  const installations = useListGithubInstallations(orgId, { query: { enabled: orgReady } });
  const notConfigured = (installations.error as ConduxApiError | null)?.status === 404;
  const linked = installations.data?.status === 200 ? installations.data.data : [];
  // The App installs once per org, and every repo operation mints its token from the first row.
  const installation = linked[0];
  const canManage = orgReady && (current.role === "owner" || current.role === "admin");

  return {
    orgId,
    loading: !orgReady || installations.isPending,
    notConfigured,
    connected: installation !== undefined,
    canManage,
    installation,
    needsReconnect:
      orgReady && !installations.isPending && !notConfigured && installation === undefined,
    refetch: installations.refetch,
  };
}
