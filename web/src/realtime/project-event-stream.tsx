"use client";

import { useQueryClient } from "@tanstack/react-query";
import { useEffect } from "react";
import { apiUrl } from "@/src/api/fetcher";
import { ProjectStatus, useCurrentProject } from "@/src/issues/current-project";

// One live Server-Sent Events connection for the current project (ADR-0030). On each "project changed"
// ping it invalidates every query for that project so open surfaces refetch at once; the counts also poll
// slowly on their own, so a dropped stream is self-healing. Opened once (via <ProjectEventStream/> in the
// sidebar) and shared by everything.
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
    //
    // Every project-scoped query, not a curated list of keys: this started as the two badge counts, and
    // the lists were left out — so a fix finished by a runner (or an issue landed by another user) moved
    // the badge while the list beside it sat stale until reload. A ping is rare (a new or regressed
    // issue, a fix run changing state) and invalidation only marks; TanStack refetches what is mounted.
    const refresh = () => {
      queryClient.invalidateQueries({
        predicate: (query) =>
          typeof query.queryKey[0] === "string" &&
          query.queryKey[0].startsWith(`/api/projects/${projectId}/`),
      });
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
