"use client";

import Link from "next/link";
import { useTranslations } from "next-intl";
import { type ReactNode, useState } from "react";
import { useFixCostRollup, useListProjectFixes } from "@/src/api/generated/condux";
import type { FixListItem } from "@/src/api/generated/model";
import { Notice } from "@/src/components/notice";
import { TriPaneSurface } from "@/src/components/tri-pane-surface";
import { ProjectStatus, useCurrentProject } from "@/src/issues/current-project";
import { isFixInFlight } from "@/src/issues/fix-format";
import { ROUTES } from "@/src/routes";
import { type FixBucket, fixBucketOf } from "./fix-views";
import { FixesListRail } from "./fixes-list-rail";
import { FixesViewsRail } from "./fixes-views-rail";

// The Fixes surface (layout 30): the lifecycle views rail, the fix list rail and the routed detail
// side by side. The active (non-archived) list always feeds the rail's bucket counts; the archived
// list is fetched only when Archived is selected. The active list polls while any run is in flight.
export function FixesSurface({ children }: { children: ReactNode }) {
  const translate = useTranslations("fixes");
  const [activeBucket, setActiveBucket] = useState<FixBucket | null>(null);
  const [archived, setArchived] = useState(false);

  const current = useCurrentProject();
  const ready = current.status === ProjectStatus.Ready;
  const projectId = ready ? current.project.id : 0;

  const activeFixes = useListProjectFixes(projectId, undefined, {
    query: {
      enabled: ready,
      refetchInterval: (query) =>
        (query.state.data?.data ?? []).some((fix) => isFixInFlight(fix.status)) ? 2000 : false,
    },
  });
  const archivedFixes = useListProjectFixes(
    projectId,
    { archived: true },
    { query: { enabled: ready && archived } },
  );
  // The project's Conductor spend over the default 30-day window, shown at the base of the views rail.
  const costRollup = useFixCostRollup(projectId, undefined, { query: { enabled: ready } });

  if (current.status === ProjectStatus.Loading) {
    return <SurfaceNotice text={translate("loadingProject")} />;
  }
  if (current.status === ProjectStatus.Error) {
    return <SurfaceNotice text={translate("projectsError")} />;
  }
  if (current.status === ProjectStatus.NoOrg) {
    return (
      <div className="p-6">
        <Notice>
          {translate("noOrg")}{" "}
          <Link href={ROUTES.onboarding} className="text-primary hover:underline">
            {translate("noOrgCta")}
          </Link>
        </Notice>
      </div>
    );
  }
  if (current.status === ProjectStatus.NoProjects) {
    return <SurfaceNotice text={translate("noProjects", { org: current.org.name })} />;
  }

  const activeList = activeFixes.data?.data ?? [];
  const archivedList = archivedFixes.data?.data ?? [];

  let visible: FixListItem[];
  let headingName: string;
  if (archived) {
    visible = archivedList;
    headingName = translate("views.archived");
  } else if (activeBucket === null) {
    visible = activeList;
    headingName = translate("views.all");
  } else {
    visible = activeList.filter((fix) => fixBucketOf(fix) === activeBucket);
    headingName = translate(`views.${activeBucket}`);
  }
  const heading = `${headingName} · ${visible.length}`;
  const listQuery = archived ? archivedFixes : activeFixes;

  return (
    <TriPaneSurface
      storageId="condux-fixes-surface"
      views={
        <FixesViewsRail
          items={activeList}
          activeBucket={activeBucket}
          onSelectBucket={(bucket) => {
            setArchived(false);
            setActiveBucket(bucket);
          }}
          archivedActive={archived}
          onSelectArchived={() => setArchived(true)}
          spend={costRollup.data?.data ?? null}
        />
      }
      list={
        <FixesListRail
          heading={heading}
          isPending={listQuery.isPending}
          isError={listQuery.isError}
          fixes={visible}
        />
      }
    >
      {children}
    </TriPaneSurface>
  );
}

function SurfaceNotice({ text }: { text: string }) {
  return (
    <div className="p-6">
      <Notice>{text}</Notice>
    </div>
  );
}
