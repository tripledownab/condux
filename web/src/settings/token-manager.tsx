"use client";

import { useFormatter, useTranslations } from "next-intl";
import { type FormEvent, type ReactNode, useState } from "react";
import { FIELD_CLASS, PRIMARY_BUTTON_CLASS, SECONDARY_BUTTON_CLASS } from "@/src/components/form";
import { Notice } from "@/src/components/notice";

// The two scoped-token surfaces (release tokens, MCP tokens) are the same machine credential shape — mint
// once, list, revoke; only the hash is stored — so this presentational component owns all of it. Callers
// wire their own hooks + i18n namespace (parallel key sets) and, optionally, a connect snippet to show
// beside the freshly minted token. Nothing here fetches; state lives in the caller.

/// A token as listed (both token kinds share this shape).
export type TokenView = { id: string; name: string; lastUsedAt?: string | null; revoked: boolean };
/// The one-time minted value (shown once, never re-fetchable).
export type MintedView = { name: string; token: string };

export function TokenManager({
  namespace,
  canManage,
  tokens,
  isPending,
  isError,
  minted,
  creating,
  revoking,
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
  onCreate: (name: string) => void;
  onRevoke: (id: string) => void;
  onDismissMinted: () => void;
  renderConnect?: (rawToken: string) => ReactNode;
}) {
  const translate = useTranslations(namespace);
  const [name, setName] = useState("");

  const submit = (event: FormEvent) => {
    event.preventDefault();
    onCreate(name.trim());
    setName("");
  };

  return (
    <section>
      <div>
        <h2 className="font-heading text-lg font-semibold text-foreground">{translate("title")}</h2>
        <p className="mt-1 text-sm text-muted-foreground">{translate("description")}</p>
      </div>

      {canManage ? (
        <form onSubmit={submit} className="mt-4 flex flex-wrap items-center gap-2">
          <input
            value={name}
            onChange={(event) => setName(event.target.value)}
            placeholder={translate("namePlaceholder")}
            aria-label={translate("nameLabel")}
            className={`${FIELD_CLASS} min-w-0 flex-1`}
          />
          <button type="submit" disabled={creating} className={PRIMARY_BUTTON_CLASS}>
            {creating ? translate("creating") : translate("create")}
          </button>
        </form>
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

// The freshly-minted token, shown once with a copy button (only the hash is stored, so this is the one
// chance to grab it); an optional connect snippet renders beneath it while the raw token is on screen.
function MintedToken({
  namespace,
  token,
  onDismiss,
  renderConnect,
}: {
  namespace: string;
  token: MintedView;
  onDismiss: () => void;
  renderConnect?: (rawToken: string) => ReactNode;
}) {
  const translate = useTranslations(namespace);
  const [copied, setCopied] = useState(false);

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(token.token);
      setCopied(true);
    } catch {
      // Clipboard unavailable (e.g. insecure context) — the value stays selectable to copy by hand.
    }
  };

  return (
    <div className="mt-3 rounded-lg border border-info/50 bg-card p-3">
      <p className="text-xs text-muted-foreground">
        {translate("mintedTitle", { name: token.name })}
      </p>
      <div className="mt-2 flex flex-wrap items-center gap-2">
        <input
          readOnly
          value={token.token}
          aria-label={translate("mintedLabel")}
          className={`${FIELD_CLASS} min-w-0 flex-1 font-mono text-xs`}
        />
        <button type="button" onClick={copy} className={SECONDARY_BUTTON_CLASS}>
          {copied ? translate("copied") : translate("copy")}
        </button>
        <button type="button" onClick={onDismiss} className={SECONDARY_BUTTON_CLASS}>
          {translate("dismiss")}
        </button>
      </div>
      <p className="mt-1 text-xs text-muted-foreground">{translate("mintedHint")}</p>
      {renderConnect?.(token.token)}
    </div>
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
        <span className="text-sm font-medium text-foreground">{token.name}</span>
        <div className="flex items-center gap-3">
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
