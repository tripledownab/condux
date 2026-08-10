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
      <ul className="mt-3 divide-y divide-border/50">
        {events.map((event) => (
          <li key={event.eventId} className="flex items-center gap-3 py-2 text-xs">
            <span
              className={`w-12 shrink-0 font-medium uppercase ${
                LEVEL_CLASS[event.level] ?? "text-muted-foreground"
              }`}
            >
              {event.level}
            </span>
            <span className="w-36 shrink-0 whitespace-nowrap text-muted-foreground tabular-nums">
              {formatTimestamp(event.timestamp)}
            </span>
            <span className="min-w-0 flex-1 truncate font-mono text-foreground">
              {event.exceptionType
                ? `${event.exceptionType}: ${event.exceptionValue}`
                : event.message}
            </span>
            <button
              type="button"
              onClick={() => onShowRaw(event)}
              className="shrink-0 text-muted-foreground transition-colors hover:text-foreground"
            >
              {translate("raw")}
            </button>
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
