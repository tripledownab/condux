"use client";

import { useFormatter, useTranslations } from "next-intl";
import type { ReactNode } from "react";
import { SECONDARY_BUTTON_CLASS } from "@/src/components/form";
import { Notice } from "@/src/components/notice";
import type { ComboboxOption } from "@/src/components/ui/combobox";
import { MintedToken, type MintedView, TokenMintForm } from "./token-mint";

// The two scoped-token surfaces (release tokens, MCP tokens) are the same machine credential shape — mint
// once, list, revoke; only the hash is stored — so this presentational component owns all of it. Callers
// wire their own hooks + i18n namespace (parallel key sets) and, optionally, a connect snippet to show
// beside the freshly minted token. Nothing here fetches; state lives in the caller. Minting lives in
// ./token-mint, which is where the optional capability picker sits.

/// A token as listed (both token kinds share this shape). `capability` is the wire name of what the token
/// may do, for the kinds that carry one; a kind without capabilities leaves it undefined.
export type TokenView = {
  id: string;
  name: string;
  lastUsedAt?: string | null;
  revoked: boolean;
  capability?: string;
};
export type { MintedView };

export function TokenManager({
  namespace,
  canManage,
  tokens,
  isPending,
  isError,
  minted,
  creating,
  revoking,
  capabilities,
  onCreate,
  onRevoke,
  onDismissMinted,
  renderConnect,
}: {
  namespace: string;
  canManage: boolean;
  tokens: TokenView[];
  isPending: boolean;
  isError: boolean;
  minted: MintedView | null;
  creating: boolean;
  revoking: boolean;
  capabilities?: readonly ComboboxOption[];
  onCreate: (name: string, capability?: string) => void;
  onRevoke: (id: string) => void;
  onDismissMinted: () => void;
  renderConnect?: (minted: MintedView) => ReactNode;
}) {
  const translate = useTranslations(namespace);

  return (
    <section>
      <div>
        <h2 className="font-heading text-lg font-semibold text-foreground">{translate("title")}</h2>
        <p className="mt-1 text-sm text-muted-foreground">{translate("description")}</p>
      </div>

      {canManage ? (
        <TokenMintForm
          namespace={namespace}
          creating={creating}
          capabilities={capabilities}
          onCreate={onCreate}
        />
      ) : null}

      {minted ? (
        <MintedToken
          namespace={namespace}
          token={minted}
          onDismiss={onDismissMinted}
          renderConnect={renderConnect}
        />
      ) : null}

      <div className="mt-4">
        <TokenListBody
          namespace={namespace}
          isPending={isPending}
          isError={isError}
          tokens={tokens}
          canManage={canManage}
          onRevoke={onRevoke}
          revoking={revoking}
        />
      </div>
    </section>
  );
}

function TokenListBody({
  namespace,
  isPending,
  isError,
  tokens,
  canManage,
  onRevoke,
  revoking,
}: {
  namespace: string;
  isPending: boolean;
  isError: boolean;
  tokens: TokenView[];
  canManage: boolean;
  onRevoke: (id: string) => void;
  revoking: boolean;
}) {
  const translate = useTranslations(namespace);
  if (isPending) {
    return <Notice>{translate("loading")}</Notice>;
  }
  if (isError) {
    return <Notice>{translate("error")}</Notice>;
  }
  if (tokens.length === 0) {
    return <Notice>{translate("empty")}</Notice>;
  }
  return (
    <ul className="flex flex-col gap-3">
      {tokens.map((token) => (
        <TokenRow
          key={token.id}
          namespace={namespace}
          token={token}
          canManage={canManage}
          onRevoke={onRevoke}
          revoking={revoking}
        />
      ))}
    </ul>
  );
}

function TokenRow({
  namespace,
  token,
  canManage,
  onRevoke,
  revoking,
}: {
  namespace: string;
  token: TokenView;
  canManage: boolean;
  onRevoke: (id: string) => void;
  revoking: boolean;
}) {
  const translate = useTranslations(namespace);
  const format = useFormatter();
  return (
    <li className="rounded-lg border border-border bg-card p-4">
      <div className="flex items-center justify-between gap-3">
        <span className="min-w-0 truncate text-sm font-medium text-foreground">{token.name}</span>
        <div className="flex items-center gap-3">
          {/* What the token may do, so an operator can tell a writing token from a reading one at a
              glance. It cannot be edited, so this is a label rather than a control. */}
          {token.capability === undefined ? null : (
            <span className="shrink-0 rounded border border-border px-1.5 py-0.5 text-xs uppercase text-muted-foreground">
              {translate(`capability.${token.capability}`)}
            </span>
          )}
          <span
            className={`text-xs uppercase ${token.revoked ? "text-muted-foreground" : "text-info"}`}
          >
            {token.revoked ? translate("revoked") : translate("active")}
          </span>
          {canManage && !token.revoked ? (
            <button
              type="button"
              onClick={() => onRevoke(token.id)}
              disabled={revoking}
              className={SECONDARY_BUTTON_CLASS}
            >
              {translate("revoke")}
            </button>
          ) : null}
        </div>
      </div>
      <p className="mt-2 text-xs text-muted-foreground">
        {token.lastUsedAt
          ? translate("lastUsed", {
              date: format.dateTime(new Date(token.lastUsedAt), { dateStyle: "medium" }),
            })
          : translate("neverUsed")}
      </p>
    </li>
  );
}
