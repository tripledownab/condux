"use client";

import { useNewIssueCount } from "@/src/api/generated/condux";
import { ProjectStatus, useCurrentProject } from "@/src/issues/current-project";

// The count of issues new or regressed since the current user last opened the project's issue list,
// rendered on the Issues nav item (ADR-0030). Renders nothing at zero (or before a project resolves).
// The live SSE stream (opened once in the sidebar) invalidates this query the instant a new issue lands;
// it also polls slowly as a safety net. Self-contained so the sidebar stays declarative.
export function NewIssuesNavBadge({ collapsed }: { collapsed: boolean }) {
  const current = useCurrentProject();
  const ready = current.status === ProjectStatus.Ready;
  const projectId = ready ? current.project.id : 0;
  const query = useNewIssueCount(projectId, {
    query: { enabled: ready, refetchInterval: 180_000, refetchOnWindowFocus: true },
  });
  const count = query.data?.data?.count ?? 0;

  if (count === 0) {
    return null;
  }

  if (collapsed) {
    return (
      <span aria-hidden className="absolute right-1 top-1 size-2 rounded-full bg-destructive" />
    );
  }

  return (
    <span className="ml-auto rounded-full bg-destructive px-1.5 text-[10px] font-medium text-white tabular-nums">
      {count > 99 ? "99+" : count}
    </span>
  );
}
