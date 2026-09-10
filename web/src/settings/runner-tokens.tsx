"use client";

import { useQueryClient } from "@tanstack/react-query";
import { useTranslations } from "next-intl";
import { useState } from "react";
import {
  getListRunnerTokensQueryKey,
  useCreateRunnerToken,
  useListRunnerTokens,
  useRevokeRunnerToken,
} from "@/src/api/generated/condux";
import { type MintedView, TokenManager } from "./token-manager";

// Runner tokens (ADR-0033): the credential a self-hosted runner presents to lease fix work. Org-scoped,
// unlike the project-scoped release and MCP tokens, because a runner serves whatever work its org
// produces. Same mint-once / list / revoke shape, so it rides the shared TokenManager; the connect
// snippet shows how to start a runner with the freshly minted token.
export function RunnerTokens({ orgId, canManage }: { orgId: number; canManage: boolean }) {
  const queryClient = useQueryClient();
  const tokens = useListRunnerTokens(orgId);
  const createToken = useCreateRunnerToken();
  const revokeToken = useRevokeRunnerToken();
  const [minted, setMinted] = useState<MintedView | null>(null);

  const invalidate = () =>
    queryClient.invalidateQueries({ queryKey: getListRunnerTokensQueryKey(orgId) });

  return (
    <TokenManager
      namespace="settings.runnerTokens"
      canManage={canManage}
      tokens={(tokens.data?.data ?? []).map((token) => ({
        id: token.id,
        name: token.label,
        lastUsedAt: token.lastUsedAt,
        revoked: token.revoked,
      }))}
      isPending={tokens.isPending}
      isError={tokens.isError}
      minted={minted}
      creating={createToken.isPending}
      revoking={revokeToken.isPending}
      onCreate={(label) =>
        createToken.mutate(
          { orgId, data: { label: label || "Runner" } },
          {
            onSuccess: (created) => {
              setMinted({ name: created.data.label, token: created.data.token });
              invalidate();
            },
          },
        )
      }
      onRevoke={(tokenId) => revokeToken.mutate({ orgId, tokenId }, { onSuccess: invalidate })}
      onDismissMinted={() => setMinted(null)}
      renderConnect={(minted) => <RunnerConnect rawToken={minted.token} />}
    />
  );
}

// Where the runner leases from: the configured API base, else the current origin (same-origin prod).
function controlPlaneUrl(): string {
  return (
    process.env.NEXT_PUBLIC_CONDUX_API_URL ||
    (typeof window !== "undefined" ? window.location.origin : "")
  );
}

// A ready-to-paste start command, shown while the raw token is on screen. The fake provider is the
// deliberate default: it exercises the whole lease path without a model key or a git token, so the
// connection is proved before any credential is handed over.
function RunnerConnect({ rawToken }: { rawToken: string }) {
  const translate = useTranslations("settings.runnerTokens");
  const command = [
    "docker run -d --name condux-runner \\",
    `  -e CONDUX_CONTROL_PLANE_URL=${controlPlaneUrl()} \\`,
    `  -e CONDUX_RUNNER_TOKEN=${rawToken} \\`,
    "  -e CONDUX_RUNNER_PROVIDER=fake \\",
    "  condux-runner",
  ].join("\n");
  return (
    <div className="mt-3">
      <p className="text-xs text-muted-foreground">{translate("connectHint")}</p>
      <pre className="mt-1 overflow-x-auto rounded bg-background p-2 font-mono text-xs text-muted-foreground">
        {command}
      </pre>
      <p className="mt-1 text-xs text-muted-foreground">{translate("connectProviderHint")}</p>
    </div>
  );
}
