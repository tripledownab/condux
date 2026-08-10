"use client";

import { useTranslations } from "next-intl";
import { useAdminOverview } from "@/src/api/generated/condux";
import { Notice } from "@/src/components/notice";

// The Overview tab: platform-wide totals (orgs, users, projects, issues) for the operator. Read-only.
export function AdminOverview() {
  const translate = useTranslations("admin.overview");
  const overview = useAdminOverview();

  if (overview.isPending) {
    return <Notice>{translate("loading")}</Notice>;
  }
  if (overview.isError || !overview.data?.data) {
    return <Notice>{translate("error")}</Notice>;
  }

  const stats = overview.data.data;
  return (
    <dl className="grid grid-cols-2 gap-4 sm:grid-cols-4">
      <Stat label={translate("orgs")} value={stats.orgs} />
      <Stat label={translate("users")} value={stats.users} />
      <Stat label={translate("projects")} value={stats.projects} />
      <Stat label={translate("issues")} value={stats.issues} />
    </dl>
  );
}

function Stat({ label, value }: { label: string; value: number }) {
  return (
    <div className="rounded-lg border border-border bg-card p-4">
      <dt className="text-xs uppercase text-muted-foreground">{label}</dt>
      <dd className="mt-1 font-heading text-2xl font-semibold text-foreground">{value}</dd>
    </div>
  );
}
