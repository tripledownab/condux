"use client";

import Link from "next/link";
import { useTranslations } from "next-intl";
import { Badge } from "@/src/components/ui/badge";
import { OrgStatus, useCurrentOrg } from "@/src/orgs/current-org";
import { planName } from "@/src/orgs/plan-format";
import { ROUTES } from "@/src/routes";

// The org's current plan at the base of the sidebar (beneath the collapse toggle), as a badge that links to
// Settings where the plan can be changed — so the tier is glanceable from anywhere. Hidden until an org is
// resolved (e.g. during onboarding there is none yet). Collapsed: the tier's initial with a tooltip.
export function PlanBadge({ collapsed }: { collapsed: boolean }) {
  const translate = useTranslations("nav");
  const org = useCurrentOrg();
  if (org.status !== OrgStatus.Ready) {
    return null;
  }

  const name = planName(org.org.tier);
  const href = `${ROUTES.settings}/general`;

  if (collapsed) {
    return (
      <Link
        href={href}
        title={translate("planLabel", { plan: name })}
        className="flex justify-center rounded-md p-1 transition-colors hover:bg-secondary"
      >
        <Badge variant="secondary">{name[0]}</Badge>
        <span className="sr-only">{translate("planLabel", { plan: name })}</span>
      </Link>
    );
  }

  return (
    <Link
      href={href}
      className="mb-2 flex items-center justify-between rounded-md px-2 py-1.5 text-xs text-muted-foreground transition-colors hover:bg-secondary hover:text-foreground"
    >
      <span>{translate("plan")}</span>
      <Badge variant="secondary">{name}</Badge>
    </Link>
  );
}
