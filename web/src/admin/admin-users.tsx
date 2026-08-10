"use client";

import { useTranslations } from "next-intl";
import { useState } from "react";
import { useAdminListUsers } from "@/src/api/generated/condux";
import type { AdminUserResponse } from "@/src/api/generated/model";
import { ConfirmDialog } from "@/src/components/confirm-dialog";
import { SECONDARY_BUTTON_CLASS } from "@/src/components/form";
import { Notice } from "@/src/components/notice";
import { formatTimestamp } from "@/src/lib/time";
import { useImpersonateOrg } from "./use-impersonate-org";

// The Users tab: every account on the platform with its primary org, org count, and created date. Each
// user with an org gets a "View as org" action that opens a read-only session over that org (ADR-0027).
export function AdminUsers() {
  const translate = useTranslations("admin.users");
  const users = useAdminListUsers();

  if (users.isPending) {
    return <Notice>{translate("loading")}</Notice>;
  }
  if (users.isError) {
    return <Notice>{translate("error")}</Notice>;
  }
  const list = users.data?.data ?? [];
  if (list.length === 0) {
    return <Notice>{translate("empty")}</Notice>;
  }

  return (
    <div className="overflow-x-auto rounded-lg border border-border">
      <table className="w-full text-left text-sm">
        <thead className="border-b border-border bg-card text-xs uppercase text-muted-foreground">
          <tr>
            <th className="px-4 py-2 font-medium">{translate("email")}</th>
            <th className="px-4 py-2 font-medium">{translate("organization")}</th>
            <th className="px-4 py-2 text-right font-medium">{translate("orgs")}</th>
            <th className="px-4 py-2 font-medium">{translate("created")}</th>
            <th className="px-4 py-2 text-right font-medium">{translate("actions")}</th>
          </tr>
        </thead>
        <tbody>
          {list.map((user) => (
            <UserRow key={user.id} user={user} />
          ))}
        </tbody>
      </table>
    </div>
  );
}

function UserRow({ user }: { user: AdminUserResponse }) {
  const translate = useTranslations("admin.users");
  const impersonate = useImpersonateOrg();
  const [confirmOpen, setConfirmOpen] = useState(false);

  return (
    <tr className="border-b border-border last:border-0">
      <td className="px-4 py-2 text-foreground">{user.email}</td>
      <td className="px-4 py-2 text-muted-foreground">{user.orgName ?? "—"}</td>
      <td className="px-4 py-2 text-right text-foreground">{user.orgCount}</td>
      <td className="px-4 py-2 text-muted-foreground">{formatTimestamp(user.createdAt)}</td>
      <td className="px-4 py-2 text-right">
        {user.orgId !== null ? (
          <>
            <button
              type="button"
              onClick={() => setConfirmOpen(true)}
              disabled={impersonate.pending}
              className={SECONDARY_BUTTON_CLASS}
            >
              {translate("viewAs")}
            </button>
            <ConfirmDialog
              open={confirmOpen}
              onOpenChange={setConfirmOpen}
              title={translate("viewAsConfirmTitle")}
              body={translate("viewAsConfirmBody", { org: user.orgName ?? "" })}
              confirmLabel={translate("viewAs")}
              cancelLabel={translate("cancel")}
              pending={impersonate.pending}
              onConfirm={() => impersonate.start(user.orgId as number)}
            />
          </>
        ) : null}
      </td>
    </tr>
  );
}
