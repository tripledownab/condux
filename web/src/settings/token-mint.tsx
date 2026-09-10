"use client";

import { useTranslations } from "next-intl";
import { type FormEvent, type ReactNode, useState } from "react";
import { FIELD_CLASS, PRIMARY_BUTTON_CLASS, SECONDARY_BUTTON_CLASS } from "@/src/components/form";
import { Combobox, type ComboboxOption } from "@/src/components/ui/combobox";

// Minting half of the scoped-token surfaces: the create form and the one-time reveal of the minted value.
// Split out of TokenManager, which owns the listing, because the file had grown past the size limit and
// this is the seam that was already there. Presentational, like its parent: nothing here fetches.

/// The one-time minted value (shown once, never re-fetchable). `capability` is set for the token kinds
/// that carry one, so the connect snippet can say what the token being pasted is allowed to do.
export type MintedView = { name: string; token: string; capability?: string };

export function TokenMintForm({
  namespace,
  creating,
  capabilities,
  onCreate,
}: {
  namespace: string;
  creating: boolean;
  // Omitted by a token kind whose authority is fixed by which endpoint accepts it (release tokens), so no
  // picker renders and onCreate is called with no capability. THE FIRST OPTION IS THE DEFAULT, so the
  // least authority is selected by listing it first rather than by a second prop that could disagree
  // with the list.
  capabilities?: readonly ComboboxOption[];
  onCreate: (name: string, capability?: string) => void;
}) {
  const translate = useTranslations(namespace);
  const translateCommon = useTranslations("common");
  const initial = capabilities?.[0]?.value ?? "";
  const [name, setName] = useState("");
  const [capability, setCapability] = useState(initial);

  const submit = (event: FormEvent) => {
    event.preventDefault();
    onCreate(name.trim(), capabilities === undefined ? undefined : capability);
    setName("");
    setCapability(initial);
  };

  return (
    <form onSubmit={submit} className="mt-4 flex flex-wrap items-center gap-2">
      <input
        value={name}
        onChange={(event) => setName(event.target.value)}
        placeholder={translate("namePlaceholder")}
        aria-label={translate("nameLabel")}
        className={`${FIELD_CLASS} min-w-0 flex-1`}
      />
      {capabilities === undefined ? null : (
        <Combobox
          value={capability}
          onValueChange={setCapability}
          options={capabilities}
          aria-label={translate("capabilityLabel")}
          searchPlaceholder={translateCommon("comboboxSearch")}
          emptyText={translateCommon("comboboxEmpty")}
          className="w-44"
        />
      )}
      <button type="submit" disabled={creating} className={PRIMARY_BUTTON_CLASS}>
        {creating ? translate("creating") : translate("create")}
      </button>
    </form>
  );
}

// The freshly-minted token, shown once with a copy button (only the hash is stored, so this is the one
// chance to grab it); an optional connect snippet renders beneath it while the raw token is on screen.
export function MintedToken({
  namespace,
  token,
  onDismiss,
  renderConnect,
}: {
  namespace: string;
  token: MintedView;
  onDismiss: () => void;
  renderConnect?: (minted: MintedView) => ReactNode;
}) {
  const translate = useTranslations(namespace);
  const [copied, setCopied] = useState(false);

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(token.token);
      setCopied(true);
    } catch {
      // Clipboard unavailable (e.g. insecure context). The value stays selectable to copy by hand.
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
      {renderConnect?.(token)}
    </div>
  );
}
