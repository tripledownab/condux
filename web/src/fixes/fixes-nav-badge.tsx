"use client";

import { useUnviewedFixCount } from "@/src/api/generated/condux";
import { ProjectStatus, useCurrentProject } from "@/src/issues/current-project";

// The unviewed needs-attention count for the current project, rendered on the Fixes nav item. Renders
// nothing when the count is zero (or no project is resolved yet). Self-contained so the sidebar stays
// declarative and its test does not need the project/count wiring.
export function FixesNavBadge({ collapsed }: { collapsed: boolean }) {
  const current = useCurrentProject();
  const ready = current.status === ProjectStatus.Ready;
  const projectId = ready ? current.project.id : 0;
  const query = useUnviewedFixCount(projectId, { query: { enabled: ready } });
  const count = query.data?.data?.count ?? 0;

  if (count === 0) {
    return null;
  }

  if (collapsed) {
    return <span aria-hidden className="absolute right-1 top-1 size-2 rounded-full bg-primary" />;
  }

  return (
    <span className="ml-auto rounded-full bg-primary px-1.5 text-[10px] font-medium tabular-nums text-primary-foreground">
      {count}
    </span>
  );
}
