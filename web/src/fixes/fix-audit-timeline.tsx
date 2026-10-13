"use client";

import { useTranslations } from "next-intl";
import type { FixAuditEntry } from "@/src/api/generated/model";
import { formatRelativeTime } from "@/src/lib/time";

// A fix run's append-only audit trail, oldest first: what happened, who did it, and when.
export function AuditTimeline({ audit }: { audit: FixAuditEntry[] }) {
  const translate = useTranslations("fixes.detail");
  if (audit.length === 0) {
    return null;
  }
  // The trail is append-only and ordered, so a row's position is part of its identity; the key is
  // stamped here rather than from the map callback (an index key in JSX is a lint smell).
  const rows = audit.map((entry, index) => ({ entry, key: `${index}-${entry.event}` }));
  return (
    <section className="mt-5">
      <h2 className="text-xs font-medium uppercase text-muted-foreground">
        {translate("timeline")}
      </h2>
      <ol className="mt-2 flex flex-col gap-2">
        {rows.map(({ entry, key }) => (
          <li key={key} className="flex items-baseline gap-2 text-xs">
            <span className="text-foreground">{translate(`audit.${entry.event}`)}</span>
            <span className="text-muted-foreground">{entry.actor}</span>
            <span className="ml-auto text-muted-foreground">
              {formatRelativeTime(entry.createdAt)}
            </span>
          </li>
        ))}
      </ol>
    </section>
  );
}
