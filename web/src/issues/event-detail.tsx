"use client";

import { useTranslations } from "next-intl";
import type { ReactNode } from "react";
import { formatTimestamp } from "@/src/lib/time";
import type { ParsedEvent, ParsedFrame } from "./event-payload";
import { primaryException } from "./event-payload";

// Resolves a frame to its GitHub blob URL; null when no repo/mapping covers it.
export type FrameUrlResolver = (frame: ParsedFrame) => string | null;

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

// Newest (crashing) frame first, like Sentry; in-app frames carry the accent border and full opacity.
function StackTrace({ event, frameUrl }: { event: ParsedEvent; frameUrl?: FrameUrlResolver }) {
  const translate = useTranslations("issues.event");
  const primary = primaryException(event);
  if (primary === undefined || primary.frames.length === 0) {
    return null;
  }

  // A stack is render-only and positional — the same frame can repeat (recursion), so the position is
  // part of each row's identity, stamped here rather than taken from the map callback. Source lines
  // are keyed by their absolute line number, derived from the culprit's lineno.
  const frames = [...primary.frames].reverse().map((frame, position) => {
    const url = frameUrl?.(frame) ?? null;
    const before = frame.contextBefore.map((text, offset) => ({
      key: `pre-${frame.lineno - frame.contextBefore.length + offset}`,
      number: frame.lineno > 0 ? frame.lineno - frame.contextBefore.length + offset : 0,
      text,
    }));
    const after = frame.contextAfter.map((text, offset) => ({
      key: `post-${frame.lineno + 1 + offset}`,
      number: frame.lineno > 0 ? frame.lineno + 1 + offset : 0,
      text,
    }));
    const vars = Object.entries(frame.vars).map(([name, value]) => ({ name, value }));
    return {
      ...frame,
      before,
      after,
      vars,
      url,
      key: `${position}-${frame.filename}-${frame.lineno}`,
    };
  });

  // Layout 30's code-path treatment: in-app frames open as source cards — surrounding context with
  // the culprit line hot behind its line number, the frame's locals underneath — while library
  // frames collapse to a dim one-liner.
  return (
    <div>
      <h4 className="text-xs font-medium uppercase text-muted-foreground">
        {translate("stackTrace")}
      </h4>
      <ol className="mt-2 flex flex-col font-mono text-xs">
        {frames.map((frame, position) => (
          <li key={frame.key}>
            {position > 0 ? (
              // The frame below called the one above; the arrow points along the call, toward the crash.
              <div aria-hidden="true" className="py-0.5 pl-4 text-muted-foreground/50">
                &#8593;
              </div>
            ) : null}
            {frame.inApp ? (
              <div className="overflow-hidden rounded-lg bg-card/60">
                <div className="flex items-baseline gap-2 px-3 py-1.5">
                  <span className="font-medium text-foreground">{frame.function ?? "?"}</span>
                  {frame.url !== null ? (
                    <a
                      href={frame.url}
                      target="_blank"
                      rel="noopener noreferrer"
                      title={translate("openInGitHub")}
                      className="min-w-0 truncate text-muted-foreground transition-colors hover:text-primary hover:underline"
                    >
                      {frame.filename ?? "?"}
                      {frame.lineno > 0 ? `:${frame.lineno}` : ""}
                    </a>
                  ) : (
                    <span className="min-w-0 truncate text-muted-foreground">
                      {frame.filename ?? "?"}
                      {frame.lineno > 0 ? `:${frame.lineno}` : ""}
                    </span>
                  )}
                  <span className="ml-auto shrink-0 rounded bg-primary/15 px-1.5 py-0.5 text-[10px] uppercase text-primary">
                    {translate("inApp")}
                  </span>
                </div>
                {frame.contextLine || frame.before.length > 0 ? (
                  <div className="py-1">
                    {frame.before.map((line) => (
                      <SourceLine key={line.key} number={line.number} text={line.text} />
                    ))}
                    {frame.contextLine ? (
                      <div className="flex border-l-2 border-error bg-error/10">
                        <span className="w-10 shrink-0 select-none pr-3 text-right font-medium tabular-nums text-error">
                          {frame.lineno > 0 ? frame.lineno : ""}
                        </span>
                        <pre className="min-w-0 flex-1 overflow-x-auto whitespace-pre text-foreground">
                          {frame.contextLine.trimEnd()}
                        </pre>
                      </div>
                    ) : null}
                    {frame.after.map((line) => (
                      <SourceLine key={line.key} number={line.number} text={line.text} />
                    ))}
                  </div>
                ) : null}
                {frame.vars.length > 0 ? (
                  <div className="px-3 py-2">
                    <span className="text-[10px] uppercase text-muted-foreground">
                      {translate("locals")}
                    </span>
                    <dl className="mt-1 flex flex-col gap-0.5">
                      {frame.vars.map((entry) => (
                        <div key={entry.name} className="flex gap-2">
                          <dt className="shrink-0 text-info">{entry.name}</dt>
                          <dd
                            className={`min-w-0 truncate ${
                              entry.value.includes("None") || entry.value.includes("undefined")
                                ? "text-error"
                                : "text-muted-foreground"
                            }`}
                          >
                            = {entry.value}
                          </dd>
                        </div>
                      ))}
                    </dl>
                  </div>
                ) : null}
              </div>
            ) : (
              <div className="px-4 py-0.5 text-muted-foreground opacity-70">
                {frame.function ?? "?"} <span>{frame.filename ?? "?"}</span>
                {frame.lineno > 0 ? `:${frame.lineno}` : ""}
              </div>
            )}
          </li>
        ))}
      </ol>
    </div>
  );
}

function SourceLine({ number, text }: { number: number; text: string }) {
  return (
    <div className="flex border-l-2 border-transparent">
      <span className="w-10 shrink-0 select-none pr-3 text-right tabular-nums text-muted-foreground/60">
        {number > 0 ? number : ""}
      </span>
      <pre className="min-w-0 flex-1 overflow-x-auto whitespace-pre text-muted-foreground">
        {text.trimEnd()}
      </pre>
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
