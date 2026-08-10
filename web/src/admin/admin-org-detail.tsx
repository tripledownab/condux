"use client";

import { useTranslations } from "next-intl";
import { useAdminOrgDetail } from "@/src/api/generated/condux";
import { Notice } from "@/src/components/notice";
import { AdminImpersonateButton } from "./admin-impersonate-button";
import { AdminOrgBilling } from "./admin-org-billing";
import { AdminOrgInfoCard } from "./admin-org-info-card";
import { AdminOrgMembers } from "./admin-org-members";
import { AdminOrgSpendMeter } from "./admin-org-spend-meter";

// One org as the platform operator manages it (ADR-0027): editable info, subscription control, spend, its
// members, and a read-only "view as org" action. Stacked sections, each owning its own data + mutations.
export function AdminOrgDetail({ orgId }: { orgId: number }) {
  const translate = useTranslations("admin.orgDetail");
  const detail = useAdminOrgDetail(orgId);

  if (detail.isPending) {
    return <Notice>{translate("loading")}</Notice>;
  }
  if (detail.isError || !detail.data?.data) {
    return <Notice>{translate("error")}</Notice>;
  }

  const org = detail.data.data;
  return (
    <div className="flex flex-col gap-8">
      <AdminOrgInfoCard
        orgId={orgId}
        name={org.name}
        slug={org.slug}
        tier={org.tier}
        aiFixMode={org.aiFixMode}
        aiFixCostCapUsd={org.aiFixCostCapUsd}
      />
      <AdminOrgBilling orgId={orgId} />
      <AdminOrgSpendMeter orgId={orgId} capUsd={org.aiFixCostCapUsd} />
      <AdminOrgMembers orgId={orgId} />
      <AdminImpersonateButton orgId={orgId} orgName={org.name} />
    </div>
  );
}
