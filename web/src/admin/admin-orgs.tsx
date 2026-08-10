"use client";

import Link from "next/link";
import { useTranslations } from "next-intl";
import { useAdminListOrgs } from "@/src/api/generated/condux";
import { Notice } from "@/src/components/notice";
import { formatTimestamp } from "@/src/lib/time";
import { planName } from "@/src/orgs/plan-format";
import { adminOrgDetailPath } from "@/src/routes";

// The Organizations tab: every org on the platform with its owner, plan, and headline counts. Each row
// links to the org detail page (view/edit + members + billing + spend, ADR-0027).
export function AdminOrgs() {
  const translate = useTranslations("admin.orgs");
  const orgs = useAdminListOrgs();

  if (orgs.isPending) {
    return <Notice>{translate("loading")}</Notice>;
  }
  if (orgs.isError) {
    return <Notice>{translate("error")}</Notice>;
  }
  const list = orgs.data?.data ?? [];
  if (list.length === 0) {
    return <Notice>{translate("empty")}</Notice>;
  }

  return (
    <div className="overflow-x-auto rounded-lg border border-border">
      <table className="w-full text-left text-sm">
        <thead className="border-b border-border bg-card text-xs uppercase text-muted-foreground">
          <tr>
            <th className="px-4 py-2 font-medium">{translate("name")}</th>
            <th className="px-4 py-2 font-medium">{translate("owner")}</th>
            <th className="px-4 py-2 font-medium">{translate("plan")}</th>
            <th className="px-4 py-2 text-right font-medium">{translate("members")}</th>
            <th className="px-4 py-2 text-right font-medium">{translate("projects")}</th>
            <th className="px-4 py-2 font-medium">{translate("created")}</th>
          </tr>
        </thead>
        <tbody>
          {list.map((org) => (
            <tr key={org.id} className="border-b border-border last:border-0 hover:bg-secondary/40">
              <td className="px-4 py-2 text-foreground">
                <Link href={adminOrgDetailPath(org.id)} className="hover:underline">
                  {org.name}
                </Link>{" "}
                <span className="text-muted-foreground">{org.slug}</span>
              </td>
              <td className="px-4 py-2 text-foreground">
                {org.ownerEmail ?? translate("noOwner")}
              </td>
              <td className="px-4 py-2 text-foreground">{planName(org.tier)}</td>
              <td className="px-4 py-2 text-right text-foreground">{org.memberCount}</td>
              <td className="px-4 py-2 text-right text-foreground">{org.projectCount}</td>
              <td className="px-4 py-2 text-muted-foreground">{formatTimestamp(org.createdAt)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
