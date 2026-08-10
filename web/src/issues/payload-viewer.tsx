"use client";

import { RiCloseLine } from "@remixicon/react";
import { useTranslations } from "next-intl";
import { useTheme } from "next-themes";
import { Highlight, themes } from "prism-react-renderer";
import { useEffect, useState } from "react";
import { prettyJson } from "@/src/lib/json";

const SLIDE_MS = 250;

// The raw payload viewer: a panel that slides up to cover the whole issue view, with the JSON
// syntax-highlighted (prism-react-renderer), and slides back down on Escape or the close button.
// Mounted only while a payload is open; entry and exit both animate via the translate transition.
export function PayloadViewer({
  payload,
  highlights,
  onClose,
}: {
  payload: string;
  highlights: string[];
  onClose: () => void;
}) {
  const translate = useTranslations("issues.payloadViewer");
  const { resolvedTheme } = useTheme();
  // Mounts translated off-screen, then slides in; closing reverses the slide before unmounting.
  const [open, setOpen] = useState(false);

  useEffect(() => {
    const frame = requestAnimationFrame(() => setOpen(true));
    return () => cancelAnimationFrame(frame);
  }, []);

  const close = () => {
    setOpen(false);
    window.setTimeout(onClose, SLIDE_MS);
  };

  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape") close();
    };
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  });

  return (
    <div
      role="dialog"
      aria-label={translate("title")}
      className={`absolute inset-x-0 -top-px bottom-0 z-20 flex flex-col border-t border-border bg-background transition-transform duration-250 ease-out ${
        open ? "translate-y-0" : "translate-y-full"
      }`}
    >
      <div className="flex h-10 shrink-0 items-center justify-between border-b border-border px-4">
        <h2 className="text-xs font-medium uppercase text-muted-foreground">
          {translate("title")}
        </h2>
        <button
          type="button"
          onClick={close}
          className="flex items-center gap-1.5 rounded-md px-2 py-1 text-xs text-muted-foreground transition-colors hover:bg-secondary hover:text-foreground"
        >
          <RiCloseLine className="size-4" aria-hidden="true" />
          {translate("close")}
        </button>
      </div>
      <Highlight
        code={prettyJson(payload)}
        language="json"
        theme={resolvedTheme === "light" ? themes.github : themes.vsDark}
      >
        {({ style, tokens, getLineProps, getTokenProps }) => {
          // Highlight output is render-only and positional, so the position is stamped into each
          // line's and token's identity before the JSX renders it.
          // A line is hot when it carries one of the issue's terms (the exception value, an in-app
          // culprit source line), so the payload's relevant spots stand out at a glance.
          const keyedLines = tokens.map((line, lineIndex) => {
            const text = line.map((token) => token.content).join("");
            return {
              key: `line-${lineIndex}`,
              number: lineIndex + 1,
              line,
              hot: highlights.some((term) => term.length > 0 && text.includes(term)),
              tokens: line.map((token, tokenIndex) => ({ key: `token-${tokenIndex}`, token })),
            };
          });
          return (
            <pre
              style={{ ...style, backgroundColor: "transparent" }}
              className="min-h-0 flex-1 overflow-auto p-4 font-mono text-xs leading-relaxed"
            >
              {keyedLines.map((entry) => (
                <div
                  key={entry.key}
                  {...getLineProps({ line: entry.line })}
                  className={
                    entry.hot
                      ? "border-l-2 border-error bg-error/10"
                      : "border-l-2 border-transparent"
                  }
                >
                  <span
                    className={`mr-4 inline-block w-8 select-none text-right ${
                      entry.hot ? "font-medium text-error" : "text-muted-foreground/50"
                    }`}
                  >
                    {entry.number}
                  </span>
                  {entry.tokens.map((keyed) => (
                    <span key={keyed.key} {...getTokenProps({ token: keyed.token })} />
                  ))}
                </div>
              ))}
            </pre>
          );
        }}
      </Highlight>
    </div>
  );
}
