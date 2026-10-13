"use client";

// The stack trace of one event: frames newest first, in-app frames opened as source cards with their
// context and locals, library frames as one dim line each. event-detail.tsx places it on the page.

import { useTranslations } from "next-intl";
import type { ParsedEvent, ParsedFrame } from "./event-payload";
import { primaryException, toFrameLabels } from "./event-payload";

// Resolves a frame to its GitHub blob URL; null when no repo/mapping covers it.
export type FrameUrlResolver = (frame: ParsedFrame) => string | null;

// Newest (crashing) frame first, like Sentry; in-app frames carry the accent border and full opacity.
export function StackTrace({
  event,
  frameUrl,
}: {
  event: ParsedEvent;
  frameUrl?: FrameUrlResolver;
}) {
  const translate = useTranslations("issues.event");
  const primary = primaryException(event);
  if (primary === undefined || primary.frames.length === 0) {
    return null;
  }

  // A stack is render-only and positional: the same frame can repeat (recursion), so the position is
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
      label: toFrameLabels(frame),
      key: `${position}-${frame.filename}-${frame.lineno}`,
    };
  });

  // Layout 30's code-path treatment: in-app frames open as source cards, with the surrounding context,
  // the culprit line hot behind its line number and the frame's locals underneath, while library
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
                  {frame.label.name ? (
                    <span className="font-medium text-foreground">{frame.label.name}</span>
                  ) : null}
                  {frame.label.location === undefined ? null : frame.url !== null ? (
                    <a
                      href={frame.url}
                      target="_blank"
                      rel="noopener noreferrer"
                      title={translate("openInGitHub")}
                      className="min-w-0 truncate text-muted-foreground transition-colors hover:text-primary hover:underline"
                    >
                      {frame.label.location}
                    </a>
                  ) : (
                    <span className="min-w-0 truncate text-muted-foreground">
                      {frame.label.location}
                    </span>
                  )}
                  {frame.inlined ? (
                    <span className="ml-auto shrink-0 rounded bg-muted px-1.5 py-0.5 text-[10px] uppercase text-muted-foreground">
                      {translate("inlined")}
                    </span>
                  ) : null}
                  <span
                    className={`${frame.inlined ? "" : "ml-auto "}shrink-0 rounded bg-primary/15 px-1.5 py-0.5 text-[10px] uppercase text-primary`}
                  >
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
                {[frame.label.name, frame.label.location].filter(Boolean).join(" ")}
                {frame.inlined ? (
                  <span className="ml-2 text-[10px] uppercase">{translate("inlined")}</span>
                ) : null}
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
