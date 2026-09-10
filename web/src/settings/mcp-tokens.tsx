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

// The capabilities a token can be minted with (ADR-0046). TokenMintForm selects the first option, so
// listing read first is what makes the least authority the default; there is no second place saying so.
// A capability cannot be edited afterwards, which is why this is a mint-time choice and there is no
// control on the listed row.
const CAPABILITIES = ["read", "triage"] as const;

// Scoped MCP tokens for a project (ADR-0029): the credential an AI agent (Claude, Cursor, ...) presents
// as Authorization: Bearer to the MCP endpoint. Same mint-once / list / revoke shape as release tokens,
// so it rides the shared TokenManager; the differences are the connect snippet shown at mint time and the
// capability picker, since an MCP token can be read-only or allowed to triage.
export function McpTokens({ projectId, canManage }: { projectId: number; canManage: boolean }) {
  const translate = useTranslations("settings.mcpTokens");
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
      capabilities={CAPABILITIES.map((value) => ({
        value,
        label: translate(`capability.${value}`),
      }))}
      onCreate={(name, capability) =>
        createToken.mutate(
          { projectId, data: { name: name || "Agent", capability: capability ?? null } },
          {
            // The mint route can also answer 400 (an unrecognised capability), so the generated type is
            // a union. Only a 2xx reaches onSuccess, but narrow rather than cast: if that ever stops
            // holding, this shows nothing instead of rendering "undefined" as someone's token.
            onSuccess: (created) => {
              if ("token" in created.data) {
                setMinted({
                  name: created.data.name,
                  token: created.data.token,
                  // The server reports what it stored, so the snippet names the tier the token really
                  // has rather than the one the form asked for.
                  capability: created.data.capability,
                });
              }
              invalidate();
            },
          },
        )
      }
      onRevoke={(tokenId) => revokeToken.mutate({ projectId, tokenId }, { onSuccess: invalidate })}
      onDismissMinted={() => setMinted(null)}
      renderConnect={(minted) => <McpConnect minted={minted} />}
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
// It names the capability too: the tier is fixed for the token's life, so someone who pastes a read token
// into a config would otherwise discover the limit from a failing tool call rather than from here.
function McpConnect({ minted }: { minted: MintedView }) {
  const translate = useTranslations("settings.mcpTokens");
  const config = JSON.stringify(
    {
      mcpServers: {
        condux: { url: mcpEndpoint(), headers: { Authorization: `Bearer ${minted.token}` } },
      },
    },
    null,
    2,
  );
  return (
    <div className="mt-3">
      {minted.capability === undefined ? null : (
        <p className="text-xs text-muted-foreground">
          {translate("connectCapability", {
            capability: translate(`capability.${minted.capability}`),
          })}
        </p>
      )}
      <p className="text-xs text-muted-foreground">{translate("connectHint")}</p>
      <pre className="mt-1 overflow-x-auto rounded bg-background p-2 font-mono text-xs text-muted-foreground">
        {config}
      </pre>
    </div>
  );
}
