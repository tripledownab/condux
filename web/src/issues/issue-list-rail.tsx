"use client";

import Link from "next/link";
import { useParams, useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import type { IssueSummary } from "@/src/api/generated/model";
import { Combobox } from "@/src/components/ui/combobox";
import { formatRelativeTime } from "@/src/lib/time";
import { useKeyboardShortcuts } from "@/src/lib/use-keyboard-shortcuts";
import { issueDetailPath } from "@/src/routes";
import { levelGlyph, levelMeta } from "./issue-format";
import { ISSUE_SORTS, type IssueSort } from "./issue-sort";

// The issue list as the second rail of the issues surface (layout 30): compact rows the user works
// through while the detail stays open in the main pane. The active issue is derived from the route.
export function IssueListRail({
  heading,
  isPending,
  isError,
  issues,
  query,
  onQueryChange,
  sort,
  onSortChange,
  hasMore,
  loadingMore,
  onLoadMore,
}: {
  heading: string;
  isPending: boolean;
  isError: boolean;
  issues: IssueSummary[];
  query: string;
  onQueryChange: (query: string) => void;
  sort: IssueSort;
  onSortChange: (sort: IssueSort) => void;
  hasMore: boolean;
  loadingMore: boolean;
  onLoadMore: () => void;
}) {
  const translate = useTranslations("issues");
  const tCommon = useTranslations("common");
  const params = useParams<{ issueId?: string }>();
  const activeIssueId = params?.issueId;
  const router = useRouter();
  // j/k step through the list, opening each issue (the detail is route-driven, so moving = opening).
  // From no selection j starts at the top and k at the bottom; both clamp at the ends (no wrap).
  const go = (delta: number) => {
    if (issues.length === 0) {
      return;
    }
    const activeIndex = issues.findIndex((issue) => issue.id === activeIssueId);
    const from = activeIndex === -1 ? (delta > 0 ? -1 : issues.length) : activeIndex;
    const next = Math.min(Math.max(from + delta, 0), issues.length - 1);
    router.push(issueDetailPath(issues[next].id));
  };
  useKeyboardShortcuts({ j: () => go(1), k: () => go(-1) });
  const sortOptions = ISSUE_SORTS.map((option) => ({
    value: option,
    label: translate(`sort.${option}`),
  }));

  return (
    // The rail itself does not scroll; only the list below does. Scrolling the whole aside took the
    // heading and the filters with it, so the controls you triage with disappeared as soon as you moved
    // down the list.
    <aside className="flex h-full flex-col overflow-hidden bg-card/30">
      <div className="flex h-10 shrink-0 items-center border-b border-border px-3 text-xs text-muted-foreground">
        {heading}
      </div>
      {/* Filter and sort each get their own full-width row: in a narrow rail a shared row squeezed the
          search to a couple of characters and pushed the sort control off the right edge. */}
      <div className="flex shrink-0 flex-col gap-1.5 border-b border-border p-2">
        <input
          type="search"
          value={query}
          onChange={(event) => onQueryChange(event.target.value)}
          placeholder={translate("searchPlaceholder")}
          aria-label={translate("searchLabel")}
          className="w-full rounded-md border border-border bg-background px-2 py-1 font-mono text-xs text-foreground placeholder:text-muted-foreground/60 focus:outline-none focus-visible:ring-1 focus-visible:ring-ring"
        />
        <Combobox
          value={sort}
          onValueChange={(value) => onSortChange(value as IssueSort)}
          options={sortOptions}
          aria-label={translate("sortLabel")}
          searchPlaceholder={tCommon("comboboxSearch")}
          emptyText={tCommon("comboboxEmpty")}
          className="w-full text-xs text-muted-foreground"
        />
      </div>
      {/* min-h-0 is load-bearing: a flex child defaults to min-height:auto, which refuses to shrink
          below its content, so without it the list grows the rail instead of scrolling inside it. */}
      <div className="min-h-0 flex-1 overflow-y-auto">
        {isPending ? <RailNotice text={translate("loading")} /> : null}
        {isError ? <RailNotice text={translate("error")} /> : null}
        {!isPending && !isError && issues.length === 0 ? (
          <RailNotice text={translate("empty")} />
        ) : null}
        <ul>
          {issues.map((issue) => {
            const active = issue.id === activeIssueId;
            return (
              <li key={issue.id} className="border-b border-border/50">
                <Link
                  href={issueDetailPath(issue.id)}
                  aria-current={active ? "page" : undefined}
                  className={`block px-3 py-2.5 ${active ? "bg-secondary" : "hover:bg-secondary/40"}`}
                >
                  <div className="flex items-center gap-2 text-xs">
                    <span className={levelMeta(issue.level).className} aria-hidden="true">
                      {levelGlyph(issue.level)}
                    </span>
                    <span className="text-muted-foreground">
                      {formatRelativeTime(issue.lastSeen)}
                    </span>
                    <span className="ml-auto tabular-nums text-muted-foreground">
                      {issue.eventCount}×
                    </span>
                  </div>
                  <div className="mt-0.5 truncate text-xs font-medium text-foreground">
                    {issue.title}
                  </div>
                </Link>
              </li>
            );
          })}
        </ul>
        {/* Inside the scroller: it belongs at the end of the list, where you arrive after scrolling. */}
        {hasMore ? (
          <button
            type="button"
            onClick={onLoadMore}
            disabled={loadingMore}
            className="w-full border-t border-border px-3 py-2.5 text-center text-xs text-muted-foreground hover:bg-secondary/40 disabled:opacity-60"
          >
            {loadingMore ? translate("loadingMore") : translate("loadMore")}
          </button>
        ) : null}
      </div>
    </aside>
  );
}

function RailNotice({ text }: { text: string }) {
  return <p className="px-3 py-3 text-xs text-muted-foreground">{text}</p>;
}
