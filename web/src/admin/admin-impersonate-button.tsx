"use client";

import { useTranslations } from "next-intl";
import { useState } from "react";
import { ConfirmDialog } from "@/src/components/confirm-dialog";
import { PRIMARY_BUTTON_CLASS } from "@/src/components/form";
import { useImpersonateOrg } from "./use-impersonate-org";

// Start a read-only "view as org" session from the org detail page (ADR-0027). The start + redirect logic
// lives in useImpersonateOrg (shared with the users-list action); this is just its section + confirm.
export function AdminImpersonateButton({ orgId, orgName }: { orgId: number; orgName: string }) {
  const translate = useTranslations("admin.orgDetail.impersonate");
  const impersonate = useImpersonateOrg();
  const [open, setOpen] = useState(false);

  return (
    <section>
      <h2 className="font-heading text-lg font-semibold text-foreground">{translate("title")}</h2>
      <p className="mt-1 text-sm text-muted-foreground">{translate("description")}</p>
      <button
        type="button"
        onClick={() => setOpen(true)}
        disabled={impersonate.pending}
        className={`mt-3 ${PRIMARY_BUTTON_CLASS}`}
      >
        {translate("button")}
      </button>
      {impersonate.isError ? (
        <p role="alert" className="mt-2 text-sm text-error">
          {translate("failed")}
        </p>
      ) : null}

      <ConfirmDialog
        open={open}
        onOpenChange={setOpen}
        title={translate("confirmTitle")}
        body={translate("confirmBody", { org: orgName })}
        confirmLabel={translate("button")}
        cancelLabel={translate("cancel")}
        pending={impersonate.pending}
        onConfirm={() => impersonate.start(orgId)}
      />
    </section>
  );
}
