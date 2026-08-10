"use client";

import { useTranslations } from "next-intl";
import { useState } from "react";
import { FIELD_CLASS, SECONDARY_BUTTON_CLASS } from "@/src/components/form";

// The accept link for a just-created invite, with a copy button. The raw token is returned only once at
// creation (never re-fetchable), so this is the one chance to grab the link — and the primary way to invite
// when email delivery is off (no SMTP). The link uses the dashboard's own origin, where the /invite page
// lives, so it is correct in dev and prod without any absolute-URL config. Rendered only after a create
// (client-only), so reading window.location is safe.
export function InviteCreatedLink({ email, token }: { email: string; token: string }) {
  const translate = useTranslations("invites");
  const [copied, setCopied] = useState(false);
  const link = `${window.location.origin}/invite?token=${encodeURIComponent(token)}`;

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(link);
      setCopied(true);
    } catch {
      // Clipboard unavailable (e.g. insecure context) — the link stays selectable to copy by hand.
    }
  };

  return (
    <div className="mt-3 rounded-lg border border-border bg-card p-3">
      <p className="text-xs text-muted-foreground">{translate("linkTitle", { email })}</p>
      <div className="mt-2 flex flex-wrap items-center gap-2">
        <input
          readOnly
          value={link}
          aria-label={translate("linkTitle", { email })}
          className={`${FIELD_CLASS} min-w-0 flex-1 font-mono text-xs`}
        />
        <button type="button" onClick={copy} className={SECONDARY_BUTTON_CLASS}>
          {copied ? translate("copied") : translate("copy")}
        </button>
      </div>
      <p className="mt-1 text-xs text-muted-foreground">{translate("linkHint")}</p>
    </div>
  );
}
