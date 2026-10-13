"use client";

// One sampled event as the issue page shows it: its metadata chips and its breadcrumb trail, with the
// stack trace from stack-trace.tsx placed between them.

import { useTranslations } from "next-intl";
import type { ReactNode } from "react";
import { formatTimestamp } from "@/src/lib/time";
import type { ParsedEvent } from "./event-payload";
import { primaryException } from "./event-payload";
import { type FrameUrlResolver, StackTrace } from "./stack-trace";

// The structured body of one sampled event: metadata chips, the stack trace (newest frame first,
// in-app frames highlighted), and the breadcrumb trail. Rendered from the parsed payload. The pieces
// export individually so the issue page can interleave them with the chart, lab 30 style.
export function EventDetail({ event }: { event: ParsedEvent }) {
  return (
    <div className="mt-3 flex flex-col gap-4">
      <EventChips event={event} />
      <StackTrace event={event} />
      <Breadcrumbs event={event} />
    </div>
  );
}

// The stack trace and breadcrumb trail of one event, without the chips (the issue page places the
// chart between them).
export function EventEvidence({
  event,
  frameUrl,
}: {
  event: ParsedEvent;
  frameUrl?: FrameUrlResolver;
}) {
  return (
    <div className="flex flex-col gap-4">
      <StackTrace event={event} frameUrl={frameUrl} />
      <Breadcrumbs event={event} />
    </div>
  );
}

// One badge/chip per notable piece of metadata; absent fields simply do not render. `leading`
// prepends issue-level labels (severity, status) to the row, split from the chips by a hairline.
export function EventChips({ event, leading }: { event: ParsedEvent; leading?: ReactNode }) {
  const translate = useTranslations("issues.event");
  const primary = primaryException(event);
  const chips: Array<{ key: string; label: string; emphasis?: boolean }> = [];

  if (primary?.handled === false) {
    chips.push({ key: "unhandled", label: translate("unhandled"), emphasis: true });
  }
  if (event.request?.url) {
    chips.push({
      key: "request",
      label: `${event.request.method ?? "GET"} ${event.request.url}`,
    });
  }
  for (const [name, value] of Object.entries(event.contexts)) {
    chips.push({ key: `ctx-${name}`, label: value });
  }
  if (event.user?.username ?? event.user?.id) {
    chips.push({
      key: "user",
      label: translate("user", { user: event.user.username ?? event.user.id ?? "" }),
    });
  }
  if (event.release) {
    chips.push({ key: "release", label: event.release + (event.dist ? ` (${event.dist})` : "") });
  }
  if (event.environment) {
    chips.push({ key: "environment", label: event.environment });
  }
  if (event.serverName) {
    chips.push({ key: "server", label: event.serverName });
  }
  if (event.transaction) {
    chips.push({ key: "transaction", label: event.transaction });
  }
  for (const [name, value] of Object.entries(event.tags)) {
    chips.push({ key: `tag-${name}`, label: `${name}: ${value}` });
  }
  if (event.sdkName) {
    chips.push({ key: "sdk", label: `${event.sdkName} ${event.sdkVersion ?? ""}`.trim() });
  }

  if (chips.length === 0 && leading === undefined) {
    return null;
  }
  return (
    <div className="flex flex-wrap items-center gap-1.5">
      {leading}
      {leading !== undefined && chips.length > 0 ? (
        <span aria-hidden="true" className="mx-1 h-4 w-px bg-border" />
      ) : null}
      {chips.map((chip) => (
        <span
          key={chip.key}
          className={`rounded px-2 py-0.5 text-xs ${
            chip.emphasis
              ? "bg-error/15 font-medium text-error"
              : "bg-secondary text-muted-foreground"
          }`}
        >
          {chip.label}
        </span>
      ))}
    </div>
  );
}

function Breadcrumbs({ event }: { event: ParsedEvent }) {
  const translate = useTranslations("issues.event");
  if (event.breadcrumbs.length === 0) {
    return null;
  }

  // The trail is render-only and positional; identical crumbs can repeat within one millisecond, so
  // the position is stamped into each row's identity.
  const crumbs = event.breadcrumbs.map((crumb, position) => ({
    ...crumb,
    key: `${position}-${crumb.timestampUnixMs}`,
  }));

  return (
    <div>
      <h4 className="text-xs font-medium uppercase text-muted-foreground">
        {translate("breadcrumbs")}
      </h4>
      <ol className="mt-2 flex flex-col gap-1 text-xs">
        {crumbs.map((crumb) => (
          <li key={crumb.key} className="flex items-baseline gap-2">
            <span className="shrink-0 tabular-nums text-muted-foreground">
              {crumb.timestampUnixMs > 0
                ? formatTimestamp(new Date(crumb.timestampUnixMs).toISOString())
                : ""}
            </span>
            {crumb.category ? (
              <span className="shrink-0 text-muted-foreground">{crumb.category}</span>
            ) : null}
            <span className="min-w-0 truncate text-foreground">{crumb.message}</span>
            {Object.entries(crumb.data).map(([key, value]) => (
              <span key={key} className="shrink-0 text-muted-foreground">
                {key}={value}
              </span>
            ))}
          </li>
        ))}
      </ol>
    </div>
  );
}
