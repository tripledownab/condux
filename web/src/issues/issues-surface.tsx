"use client";

import { keepPreviousData, useQueryClient } from "@tanstack/react-query";
import Link from "next/link";
import { useTranslations } from "next-intl";
import { type ReactNode, useEffect, useState } from "react";
import {
  getListSavedViewsQueryKey,
  getNewIssueCountQueryKey,
  useCreateSavedView,
  useDeleteSavedView,
  useListIssueCounts,
  useListIssues,
  useListSavedViews,
  useMarkIssuesSeen,
} from "@/src/api/generated/condux";
import type { SavedView } from "@/src/api/generated/model";
import { Notice } from "@/src/components/notice";
import { TriPaneSurface } from "@/src/components/tri-pane-surface";
import { ROUTES } from "@/src/routes";
import { ProjectStatus, useCurrentProject } from "./current-project";
import { levelMeta } from "./issue-format";
import { IssueListRail } from "./issue-list-rail";
import { IssueSort } from "./issue-sort";
import { BUILTIN_VIEWS } from "./issue-views";
import { ShortcutsHelp } from "./shortcuts-help";
import { ViewsRail } from "./views-rail";

// How many issues one page shows; "Load more" grows the fetch (offset stays 0, the whole page is
// re-read) so the list stays a bounded subset instead of the entire project. The server bounds a page at
// 100 (PageParams.MaxLimit), so the grow stops there — a larger project is narrowed with the search.
const PAGE_SIZE = 50;
const MAX_PAGE = 100;

// The status a built-in view's query filters to, so its rail count reads straight off the status facet.
const VIEW_STATUS: Record<string, number> = {
  "is:unresolved": 1,
  "is:resolved": 2,
  "is:ignored": 3,
};

// The issues surface (layout 30): views rail, the issue list rail and the routed page side by side.
// One state model drives everything: the search query + ordering are the single source of truth, and
// a view (built-in preset or the user's saved view) is just a stored query/sort — the active view is
// whichever one matches the current state. Filtering, ordering and paging happen server-side (the
// list never loads more than a page); the rails' totals come from a companion counts endpoint.
export function IssuesSurface({ children }: { children: ReactNode }) {
  const translate = useTranslations("issues");
  const [query, setQuery] = useState("is:unresolved");
  const [sort, setSort] = useState(IssueSort.LastSeen);
  // A severity click narrows the current list to one level; clicking it again clears.
  const [levelFilter, setLevelFilter] = useState<number | null>(null);
  const [limit, setLimit] = useState(PAGE_SIZE);
  // Any change to what we are asking for resets paging back to the first page.
  // biome-ignore lint/correctness/useExhaustiveDependencies: reset paging when the request shape changes
  useEffect(() => setLimit(PAGE_SIZE), [query, sort, levelFilter]);

  const queryClient = useQueryClient();
  const current = useCurrentProject();
  const ready = current.status === ProjectStatus.Ready;
  const projectId = ready ? current.project.id : 0;

  // The severity narrow rides the same token grammar the server parses, so it composes with the query.
  const levelToken = levelFilter === null ? "" : `level:${levelMeta(levelFilter).key}`;
  const listQuery = [query.trim(), levelToken].filter(Boolean).join(" ");
  const issues = useListIssues(
    projectId,
    { q: listQuery || undefined, sort, limit },
    { query: { enabled: ready, placeholderData: keepPreviousData } },
  );
  // Counts scope to the free-text search only; the facet they break down (status / level) is excluded
  // server-side, so switching view or severity does not change the totals shown beside the other rows.
  const counts = useListIssueCounts(
    projectId,
    { q: query.trim() || undefined },
    { query: { enabled: ready, placeholderData: keepPreviousData } },
  );
  const savedViews = useListSavedViews(projectId, { query: { enabled: ready } });

  // Opening the issues list clears this user's "new issues" nav badge (ADR-0030): stamp the watermark
  // once the project resolves, then refetch the count so the badge drops immediately.
  const markSeen = useMarkIssuesSeen();
  // biome-ignore lint/correctness/useExhaustiveDependencies: mark seen once when the project becomes ready
  useEffect(() => {
    if (!ready) {
      return;
    }
    markSeen.mutate(
      { projectId },
      {
        onSuccess: () =>
          queryClient.invalidateQueries({ queryKey: getNewIssueCountQueryKey(projectId) }),
      },
    );
  }, [ready, projectId]);

  const invalidateViews = () =>
    queryClient.invalidateQueries({ queryKey: getListSavedViewsQueryKey(projectId) });
  const createView = useCreateSavedView({ mutation: { onSuccess: invalidateViews } });
  const deleteView = useDeleteSavedView({ mutation: { onSuccess: invalidateViews } });

  // Project resolution problems replace the whole surface; the rails only make sense with a project.
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

  // The response is a union (200 page | 400 error, though the fetcher throws on 400); narrow to the page.
  const listData = issues.data?.data;
  const page = listData && "issues" in listData ? listData : undefined;
  const visible = page?.issues ?? [];
  const hasMore = page?.hasMore ?? false;

  const statusCounts = counts.data?.data.byStatus;
  const levelCounts = counts.data?.data.byLevel;
  const builtinCount = (viewQuery: string): number | null => {
    if (statusCounts === undefined) {
      return null;
    }
    const status = VIEW_STATUS[viewQuery];
    return status === undefined
      ? Object.values(statusCounts).reduce((sum, n) => sum + n, 0)
      : (statusCounts[String(status)] ?? 0);
  };

  // The active view is derived, never stored: whichever preset/saved view matches the state.
  const trimmedQuery = query.trim();
  const savedList = savedViews.data?.data ?? [];
  const activeSaved =
    savedList.find((view) => view.query === trimmedQuery && view.sort === sort) ?? null;
  const activeBuiltin =
    activeSaved === null
      ? (BUILTIN_VIEWS.find((view) => view.query === trimmedQuery) ?? null)
      : null;
  const headingName =
    activeSaved?.name ??
    (activeBuiltin !== null ? translate(`views.${activeBuiltin.key}`) : translate("views.custom"));
  const shown = `${visible.length}${hasMore ? "+" : ""}`;
  const heading = `${headingName} · ${shown} · ${translate(`sortedBy.${sort}`)}`;

  return (
    <>
      <TriPaneSurface
        storageId="condux-issues-surface"
        views={
          <ViewsRail
            levelCounts={levelCounts}
            levelFilter={levelFilter}
            onLevelFilterChange={setLevelFilter}
            activeBuiltinKey={activeBuiltin?.key ?? null}
            builtinCount={builtinCount}
            onSelectBuiltin={setQuery}
            savedViews={savedList}
            activeSavedId={activeSaved?.id ?? null}
            onSelectSaved={(view: SavedView) => {
              setQuery(view.query);
              setSort(view.sort as IssueSort);
            }}
            onSaveCurrent={(name) =>
              createView.mutate({ projectId, data: { name, query: trimmedQuery, sort } })
            }
            onDeleteSaved={(id) => deleteView.mutate({ projectId, viewId: id })}
            savePending={createView.isPending}
          />
        }
        list={
          <IssueListRail
            heading={heading}
            isPending={issues.isPending}
            isError={issues.isError}
            issues={visible}
            query={query}
            onQueryChange={setQuery}
            sort={sort}
            onSortChange={setSort}
            hasMore={hasMore && limit < MAX_PAGE}
            loadingMore={issues.isFetching}
            onLoadMore={() => setLimit((current) => Math.min(current + PAGE_SIZE, MAX_PAGE))}
          />
        }
      >
        {children}
      </TriPaneSurface>
      <ShortcutsHelp />
    </>
  );
}

function SurfaceNotice({ text }: { text: string }) {
  return (
    <div className="p-6">
      <Notice>{text}</Notice>
    </div>
  );
}
