"use client";

import { useTranslations } from "next-intl";
import { useState } from "react";
import { useListIssueEvents } from "@/src/api/generated/condux";
import type { StoredEvent } from "@/src/api/generated/model";
import { Button } from "@/src/components/ui/button";
import { formatTimestamp } from "@/src/lib/time";

const PAGE_SIZE = 25;
// The server bounds a page at 100 (PageParams.MaxLimit); "Load more" grows up to that, then stops.
const MAX_PAGE = 100;

const LEVEL_CLASS: Record<string, string> = {
  fatal: "text-fatal",
  error: "text-error",
  warning: "text-warning",
  info: "text-info",
  debug: "text-debug",
};

// The issue's older sampled events as a compact, lazy-loaded table (level · time · type/message · raw).
// Only the latest event rides the detail payload and is featured above; the rest load here on demand
// (from offset 1), so a busy issue's detail page stays light. "Load more" grows the window.
export function EventsTable({
  projectId,
  issueId,
  onShowRaw,
}: {
  projectId: number;
  issueId: string;
  onShowRaw: (event: StoredEvent) => void;
}) {
  const translate = useTranslations("issues.eventsTable");
  const [limit, setLimit] = useState(PAGE_SIZE);
  const query = useListIssueEvents(projectId, issueId, { limit, offset: 1 });
  // The response is a union (200 page | 400 error, though the fetcher throws on 400); narrow to the page.
  const data = query.data?.data;
  const page = data && "events" in data ? data : undefined;
  const events = page?.events ?? [];

  // No section until there is at least one older event (a single-event issue shows only the inline latest).
  if (events.length === 0) {
    return null;
  }

  return (
    <section className="mt-8">
      <h2 className="font-heading text-lg font-semibold text-foreground">{translate("title")}</h2>
      {/* Each row is a subgrid of the list, so a column is as wide as its widest value in any row:
          a fixed width let a long level or timestamp paint over the column beside it. */}
      <ul className="mt-3 grid grid-cols-[auto_auto_minmax(0,1fr)_auto] divide-y divide-border/50">
        {events.map((event) => (
          <li
            key={event.eventId}
            className="col-span-full grid grid-cols-subgrid items-center gap-x-3 py-2 text-xs"
          >
            <span
              className={`whitespace-nowrap font-medium uppercase ${
                LEVEL_CLASS[event.level] ?? "text-muted-foreground"
              }`}
            >
              {event.level}
            </span>
            <span className="whitespace-nowrap text-muted-foreground tabular-nums">
              {formatTimestamp(event.timestamp)}
            </span>
            <span className="min-w-0 truncate font-mono text-foreground">
              {event.exceptionType
                ? `${event.exceptionType}: ${event.exceptionValue}`
                : event.message}
            </span>
            <Button type="button" variant="secondary" size="xs" onClick={() => onShowRaw(event)}>
              {translate("raw")}
            </Button>
          </li>
        ))}
      </ul>
      {page?.hasMore && limit < MAX_PAGE ? (
        <Button
          type="button"
          variant="secondary"
          size="sm"
          disabled={query.isFetching}
          onClick={() => setLimit((current) => Math.min(current + PAGE_SIZE, MAX_PAGE))}
          className="mt-3"
        >
          {query.isFetching ? translate("loading") : translate("loadMore")}
        </Button>
      ) : null}
    </section>
  );
}
