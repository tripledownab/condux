"use client";

import { useTranslations } from "next-intl";
import { useAdminAuditLog } from "@/src/api/generated/condux";
import { Notice } from "@/src/components/notice";
import { formatTimestamp } from "@/src/lib/time";

// The Audit tab (ADR-0027): recent platform-admin actions (org edits, member changes, billing actions,
// impersonation start/stop), newest first. Read-only. The actor is always the admin's real identity.
export function AdminAudit() {
  const translate = useTranslations("admin.audit");
  const audit = useAdminAuditLog();

  if (audit.isPending) {
    return <Notice>{translate("loading")}</Notice>;
  }
  if (audit.isError) {
    return <Notice>{translate("error")}</Notice>;
  }
  const entries = audit.data?.data ?? [];
  if (entries.length === 0) {
    return <Notice>{translate("empty")}</Notice>;
  }

  return (
    <div className="overflow-x-auto rounded-lg border border-border">
      <table className="w-full text-left text-sm">
        <thead className="border-b border-border bg-card text-xs uppercase text-muted-foreground">
          <tr>
            <th className="px-4 py-2 font-medium">{translate("when")}</th>
            <th className="px-4 py-2 font-medium">{translate("actor")}</th>
            <th className="px-4 py-2 font-medium">{translate("action")}</th>
            <th className="px-4 py-2 font-medium">{translate("target")}</th>
            <th className="px-4 py-2 font-medium">{translate("detail")}</th>
          </tr>
        </thead>
        <tbody>
          {entries.map((entry) => (
            <tr key={entry.id} className="border-b border-border last:border-0 align-top">
              <td className="px-4 py-2 text-muted-foreground">
                {formatTimestamp(entry.createdAt)}
              </td>
              <td className="px-4 py-2 text-foreground">{entry.actorEmail}</td>
              <td className="px-4 py-2 font-mono text-xs text-foreground">{entry.action}</td>
              <td className="px-4 py-2 text-muted-foreground tabular-nums">
                {entry.targetOrgId !== null ? translate("org", { id: entry.targetOrgId }) : "—"}
                {entry.targetUserId !== null
                  ? ` · ${translate("user", { id: entry.targetUserId })}`
                  : ""}
              </td>
              <td className="px-4 py-2 font-mono text-xs text-muted-foreground">
                {JSON.stringify(entry.details)}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
