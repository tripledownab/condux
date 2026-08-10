"use client";

import { useTranslations } from "next-intl";
import type { ParsedEvent } from "./event-payload";
import { buildFacets, usersAffected } from "./issue-facets";

// The Sentry-style facets panel: distinct users affected plus the top values per dimension (browser,
// os, tags...), aggregated from the issue's SAMPLED events — proportions hold under sampling, absolute
// totals do not, and the panel says so.
export function FacetsPanel({ events }: { events: ParsedEvent[] }) {
  const translate = useTranslations("issues.facets");
  const users = usersAffected(events);
  const facets = buildFacets(events);

  if (users === 0 && facets.length === 0) {
    return null;
  }

  return (
    <section className="rounded-lg border border-border bg-card p-4">
      <div className="flex items-baseline justify-between gap-2">
        <h2 className="font-heading text-sm font-semibold uppercase text-muted-foreground">
          {translate("title")}
        </h2>
        <span className="text-[10px] text-muted-foreground">{translate("sampledNote")}</span>
      </div>

      {users > 0 ? (
        <p className="mt-2 text-sm text-foreground">
          {translate("usersAffected", { count: users })}
        </p>
      ) : null}

      {facets.length > 0 ? (
        <dl className="mt-3 grid gap-x-6 gap-y-3 text-xs sm:grid-cols-2">
          {facets.map((facet) => (
            <div key={facet.key}>
              <dt className="font-medium uppercase text-muted-foreground">{facet.key}</dt>
              <dd className="mt-1 flex flex-col gap-1">
                {facet.values.map((entry) => (
                  <div key={entry.value} className="flex items-center gap-2">
                    <span className="min-w-0 flex-1 truncate text-foreground">{entry.value}</span>
                    <div className="h-1.5 w-16 shrink-0 overflow-hidden rounded bg-secondary">
                      <div
                        className="h-full rounded bg-primary"
                        style={{ width: `${Math.round(entry.share * 100)}%` }}
                      />
                    </div>
                    <span className="w-8 shrink-0 text-right tabular-nums text-muted-foreground">
                      {Math.round(entry.share * 100)}%
                    </span>
                  </div>
                ))}
              </dd>
            </div>
          ))}
        </dl>
      ) : null}
    </section>
  );
}
