"use client";

import { useQueryClient } from "@tanstack/react-query";
import { useTranslations } from "next-intl";
import { useState } from "react";
import {
  getListMcpTokensQueryKey,
  useCreateMcpToken,
  useListMcpTokens,
  useRevokeMcpToken,
} from "@/src/api/generated/condux";
import { type MintedView, TokenManager } from "./token-manager";

// Scoped MCP tokens for a project (ADR-0029): the read-only credential an AI agent (Claude, Cursor, ...)
// presents as Authorization: Bearer to the MCP endpoint. Same mint-once / list / revoke shape as release
// tokens, so it rides the shared TokenManager; the difference is the connect snippet shown at mint time.
export function McpTokens({ projectId, canManage }: { projectId: number; canManage: boolean }) {
  const queryClient = useQueryClient();
  const tokens = useListMcpTokens(projectId);
  const createToken = useCreateMcpToken();
  const revokeToken = useRevokeMcpToken();
  const [minted, setMinted] = useState<MintedView | null>(null);

  const invalidate = () =>
    queryClient.invalidateQueries({ queryKey: getListMcpTokensQueryKey(projectId) });

  return (
    <TokenManager
      namespace="settings.mcpTokens"
      canManage={canManage}
      tokens={tokens.data?.data ?? []}
      isPending={tokens.isPending}
      isError={tokens.isError}
      minted={minted}
      creating={createToken.isPending}
      revoking={revokeToken.isPending}
      onCreate={(name) =>
        createToken.mutate(
          { projectId, data: { name: name || "Agent" } },
          {
            onSuccess: (created) => {
              setMinted({ name: created.data.name, token: created.data.token });
              invalidate();
            },
          },
        )
      }
      onRevoke={(tokenId) => revokeToken.mutate({ projectId, tokenId }, { onSuccess: invalidate })}
      onDismissMinted={() => setMinted(null)}
      renderConnect={(rawToken) => <McpConnect rawToken={rawToken} />}
    />
  );
}

// The endpoint origin: the configured API base, else the current origin (same-origin prod).
function mcpEndpoint(): string {
  const base =
    process.env.NEXT_PUBLIC_CONDUX_API_URL ||
    (typeof window !== "undefined" ? window.location.origin : "");
  return `${base}/api/mcp`;
}

// A ready-to-paste MCP client config (a remote HTTP MCP server), shown while the raw token is on screen.
function McpConnect({ rawToken }: { rawToken: string }) {
  const translate = useTranslations("settings.mcpTokens");
  const config = JSON.stringify(
    {
      mcpServers: {
        condux: { url: mcpEndpoint(), headers: { Authorization: `Bearer ${rawToken}` } },
      },
    },
    null,
    2,
  );
  return (
    <div className="mt-3">
      <p className="text-xs text-muted-foreground">{translate("connectHint")}</p>
      <pre className="mt-1 overflow-x-auto rounded bg-background p-2 font-mono text-xs text-muted-foreground">
        {config}
      </pre>
    </div>
  );
}
