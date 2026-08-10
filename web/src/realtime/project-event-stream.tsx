"use client";

import { useQueryClient } from "@tanstack/react-query";
import { useEffect } from "react";
import { apiUrl } from "@/src/api/fetcher";
import { getNewIssueCountQueryKey, getUnviewedFixCountQueryKey } from "@/src/api/generated/condux";
import { ProjectStatus, useCurrentProject } from "@/src/issues/current-project";

// One live Server-Sent Events connection for the current project (ADR-0030). On each "project changed"
// ping it invalidates the nav-badge count queries so they refetch at once; the counts also poll slowly on
// their own, so a dropped stream is self-healing. Opened once (via <ProjectEventStream/> in the sidebar)
// and shared by every badge, so adding another badge is just another query key here.
export function useProjectEventStream(projectId: number | null) {
  const queryClient = useQueryClient();
  useEffect(() => {
    // Guard for non-browser environments (SSR, jsdom tests) where EventSource is absent.
    if (!projectId || typeof EventSource === "undefined") {
      return;
    }
    const source = new EventSource(apiUrl(`/api/projects/${projectId}/events`), {
      withCredentials: true,
    });
    // A message means "something changed, refetch". onopen fires on every (re)connect, so it also catches
    // anything that landed while the stream was down.
    const refresh = () => {
      queryClient.invalidateQueries({ queryKey: getNewIssueCountQueryKey(projectId) });
      queryClient.invalidateQueries({ queryKey: getUnviewedFixCountQueryKey(projectId) });
    };
    source.onmessage = refresh;
    source.onopen = refresh;
    return () => source.close();
  }, [projectId, queryClient]);
}

// Renders nothing; just holds the shell's single project event stream. Kept self-contained (resolves the
// current project itself) so the sidebar stays declarative wiring and its test can stub this like the
// other data-fetching children.
export function ProjectEventStream() {
  const current = useCurrentProject();
  useProjectEventStream(current.status === ProjectStatus.Ready ? current.project.id : null);
  return null;
}
