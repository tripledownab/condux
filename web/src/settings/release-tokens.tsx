"use client";

import { useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import {
  getListReleaseTokensQueryKey,
  useCreateReleaseToken,
  useListReleaseTokens,
  useRevokeReleaseToken,
} from "@/src/api/generated/condux";
import { type MintedView, TokenManager } from "./token-manager";

// Scoped release tokens for a project: the machine credential CI uses to record releases (POST
// /api/releases with Authorization: Bearer) without a login or internal ids. The raw token is shown once
// at creation and only its hash is stored, so it is never re-fetchable. Admins mint/revoke; members read.
// The mint/list/revoke UI is the shared TokenManager (also used by MCP tokens); this wires its hooks.
export function ReleaseTokens({ projectId, canManage }: { projectId: number; canManage: boolean }) {
  const queryClient = useQueryClient();
  const tokens = useListReleaseTokens(projectId);
  const createToken = useCreateReleaseToken();
  const revokeToken = useRevokeReleaseToken();
  const [minted, setMinted] = useState<MintedView | null>(null);

  const invalidate = () =>
    queryClient.invalidateQueries({ queryKey: getListReleaseTokensQueryKey(projectId) });

  return (
    <TokenManager
      namespace="settings.releaseTokens"
      canManage={canManage}
      tokens={tokens.data?.data ?? []}
      isPending={tokens.isPending}
      isError={tokens.isError}
      minted={minted}
      creating={createToken.isPending}
      revoking={revokeToken.isPending}
      onCreate={(name) =>
        createToken.mutate(
          { projectId, data: { name: name || "CI" } },
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
    />
  );
}
