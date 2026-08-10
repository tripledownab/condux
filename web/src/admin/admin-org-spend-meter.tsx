"use client";

import { useTranslations } from "next-intl";
import { useAdminOrgSpend } from "@/src/api/generated/condux";
import { formatUsd } from "@/src/lib/format";

// This org's month-to-date Conductor spend for the admin detail page (ADR-0027), against its cost cap
// when one is set. The cap comes from the org detail; spend is fetched here.
export function AdminOrgSpendMeter({ orgId, capUsd }: { orgId: number; capUsd: number | null }) {
  const translate = useTranslations("admin.orgDetail.spend");
  const spend = useAdminOrgSpend(orgId);
  const spent = spend.data?.data.totalUsd ?? 0;
  const pct = capUsd && capUsd > 0 ? Math.min(100, (spent / capUsd) * 100) : null;

  return (
    <section>
      <h2 className="font-heading text-lg font-semibold text-foreground">{translate("title")}</h2>
      <p className="mt-2 text-sm text-foreground tabular-nums">
        {capUsd !== null
          ? translate("spentOfCap", { amount: formatUsd(spent), cap: formatUsd(capUsd) })
          : translate("spent", { amount: formatUsd(spent) })}
      </p>
      {pct !== null ? (
        <div className="mt-2 h-1.5 w-full max-w-xs overflow-hidden rounded-full bg-secondary">
          <div className="h-full rounded-full bg-primary" style={{ width: `${pct}%` }} />
        </div>
      ) : null}
    </section>
  );
}
