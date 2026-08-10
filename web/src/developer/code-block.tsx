"use client";

import { useTranslations } from "next-intl";
import { useTheme } from "next-themes";
import { Highlight, Prism, themes } from "prism-react-renderer";
import { useState } from "react";
import { SECONDARY_BUTTON_CLASS } from "@/src/components/form";

// prism-react-renderer 2.x ships no dotenv grammar, so the .env snippets (KEY=value + # comments) would
// render plain. Register a tiny one on the vendored Prism the Highlight uses. Runs once on import.
Prism.languages.dotenv = {
  comment: /#.*/,
  key: { pattern: /^[ \t]*[\w.-]+(?=[ \t]*=)/m, alias: "attr-name" },
  assignment: { pattern: /=[^#\n]*/, inside: { operator: /^=/, string: /.+/ } },
};

// The snippet catalog's languages mapped to a prism-react-renderer grammar (bundled, plus the dotenv one
// registered above). C-family languages prism does not ship (Java, C#, PHP) borrow the generic clike
// grammar; anything unmapped (shell, Ruby) renders as plain text, which is fine for one-line commands.
const PRISM_LANGUAGE: Record<string, string> = {
  typescript: "typescript",
  tsx: "tsx",
  python: "python",
  go: "go",
  kotlin: "kotlin",
  dotenv: "dotenv",
  java: "clike",
  csharp: "clike",
  php: "clike",
};

// A titled, syntax-highlighted code block with a copy button (prism-react-renderer, themed by the active
// light/dark theme). Copy falls back gracefully (the code stays selectable by hand if the clipboard API
// is unavailable), matching invite-created-link.
export function CodeBlock({
  title,
  language,
  code,
}: {
  title: string;
  language?: string;
  code: string;
}) {
  const translate = useTranslations("developer");
  const { resolvedTheme } = useTheme();
  const [copied, setCopied] = useState(false);

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(code);
      setCopied(true);
    } catch {
      // Clipboard unavailable, so the code stays selectable by hand.
    }
  };

  return (
    <div className="rounded-lg border border-border bg-card">
      <div className="flex items-center justify-between gap-2 border-b border-border px-3 py-1.5">
        <span className="text-xs font-medium text-muted-foreground">{title}</span>
        <div className="flex items-center gap-2">
          {language ? <span className="text-xs text-muted-foreground">{language}</span> : null}
          <button type="button" onClick={copy} className={SECONDARY_BUTTON_CLASS}>
            {copied ? translate("copied") : translate("copy")}
          </button>
        </div>
      </div>
      <Highlight
        prism={Prism}
        code={code}
        language={PRISM_LANGUAGE[language ?? ""] ?? "text"}
        theme={resolvedTheme === "light" ? themes.github : themes.vsDark}
      >
        {({ style, tokens, getLineProps, getTokenProps }) => {
          // Highlight output is render-only and positional, so stamp the position into each line's and
          // token's key before the JSX renders it (the code never reorders).
          const keyedLines = tokens.map((line, lineIndex) => ({
            key: `line-${lineIndex}`,
            line,
            tokens: line.map((token, tokenIndex) => ({ key: `token-${tokenIndex}`, token })),
          }));
          return (
            <pre
              style={{ ...style, backgroundColor: "transparent" }}
              className="overflow-x-auto p-3 text-xs leading-relaxed"
            >
              {keyedLines.map((entry) => (
                <div key={entry.key} {...getLineProps({ line: entry.line })}>
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
