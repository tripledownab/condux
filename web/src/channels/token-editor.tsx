"use client";

import DOMPurify from "dompurify";
import { useEffect, useRef } from "react";
import { cn } from "@/src/lib/utils";

export type EditorToken = { name: string; label: string };

const TOKEN_PATTERN = /\{\{\s*(\w+)\s*\}\}/g;

type Segment = { type: "text"; text: string } | { type: "token"; name: string };

// Split a stored "{{token}}" template into text + token segments. Pure, so it is unit-testable.
export function parseTemplate(value: string): Segment[] {
  const segments: Segment[] = [];
  let last = 0;
  for (const match of value.matchAll(TOKEN_PATTERN)) {
    const index = match.index ?? 0;
    if (index > last) {
      segments.push({ type: "text", text: value.slice(last, index) });
    }
    segments.push({ type: "token", name: match[1] });
    last = index + match[0].length;
  }
  if (last < value.length) {
    segments.push({ type: "text", text: value.slice(last) });
  }
  return segments;
}

const BLOCK_TAGS = new Set(["DIV", "P"]);

// Read the editor DOM back to the stored "{{token}}" string (pill spans -> tokens, text as-is). Recurses,
// because pressing Enter makes the browser wrap a line in a <div>: a shallow walk would flatten that block
// via textContent, turning a nested pill into its bare label and dropping the line break. So instead we
// descend, emit "{{token}}" for each pill (never its textContent), and turn <br> + block boundaries into
// newlines.
export function serialize(el: HTMLElement): string {
  let out = "";
  const walk = (parent: Node) => {
    for (const node of parent.childNodes) {
      if (node.nodeType === Node.TEXT_NODE) {
        out += node.textContent ?? "";
      } else if (node instanceof HTMLElement) {
        if (node.dataset.token) {
          out += `{{${node.dataset.token}}}`;
        } else if (node.tagName === "BR") {
          // A <br> with no following sibling is the browser's filler for an empty/last line, not a real
          // break; a <br> with content after it is a genuine line break.
          if (node.nextSibling) {
            out += "\n";
          }
        } else {
          // A block element is a line the browser wrapped on Enter: start a new line before its content,
          // then recurse so its (possibly nested) pills serialize as tokens instead of flattening to text.
          if (BLOCK_TAGS.has(node.tagName) && out !== "" && !out.endsWith("\n")) {
            out += "\n";
          }
          walk(node);
        }
      }
    }
  };
  walk(el);
  return out;
}

const PILL_CLASS =
  "rounded bg-secondary px-1.5 py-0.5 align-baseline text-xs font-medium text-secondary-foreground";

function makePill(token: EditorToken): HTMLSpanElement {
  const span = document.createElement("span");
  span.dataset.token = token.name;
  span.contentEditable = "false";
  span.className = cn(PILL_CLASS, "mx-0.5");
  span.textContent = token.label;
  return span;
}

// Paint the value into the editor as text nodes + non-editable pill spans.
function paint(el: HTMLElement, value: string, byName: Map<string, EditorToken>) {
  el.replaceChildren();
  for (const segment of parseTemplate(value)) {
    if (segment.type === "text") {
      el.appendChild(document.createTextNode(segment.text));
    } else {
      const token = byName.get(segment.name);
      el.appendChild(token ? makePill(token) : document.createTextNode(`{{${segment.name}}}`));
    }
  }
}

// A text-only editor that renders {{token}} placeholders as atomic pills. Click a palette pill to insert
// one at the caret (so no one types the braces by hand). Paste is forced to sanitized plain text
// (DOMPurify), so no markup can enter. The stored value is always plain text with {{token}} placeholders;
// per-transport encoding at send time is the server's job.
export function TokenEditor({
  value,
  onChange,
  tokens,
  ariaLabel,
}: {
  value: string;
  onChange: (value: string) => void;
  tokens: EditorToken[];
  ariaLabel?: string;
}) {
  const ref = useRef<HTMLDivElement>(null);
  // null until first paint, so the initial value is always painted on mount (even the default template).
  const lastEmitted = useRef<string | null>(null);

  // Repaint only on external value changes (mount, restore default). Repainting on our own edits would
  // reset the caret to the start on every keystroke.
  useEffect(() => {
    const el = ref.current;
    if (el && value !== lastEmitted.current) {
      paint(el, value, new Map(tokens.map((token) => [token.name, token])));
      lastEmitted.current = value;
    }
  }, [value, tokens]);

  const emit = () => {
    const el = ref.current;
    if (!el) {
      return;
    }
    const next = serialize(el);
    lastEmitted.current = next;
    onChange(next);
  };

  const insertToken = (token: EditorToken) => {
    const el = ref.current;
    if (!el) {
      return;
    }
    el.focus();
    const selection = window.getSelection();
    const pill = makePill(token);
    if (selection && selection.rangeCount > 0 && el.contains(selection.anchorNode)) {
      const range = selection.getRangeAt(0);
      range.deleteContents();
      range.insertNode(pill);
      range.setStartAfter(pill);
      range.collapse(true);
      selection.removeAllRanges();
      selection.addRange(range);
    } else {
      el.appendChild(pill);
    }
    emit();
  };

  const handlePaste = (event: React.ClipboardEvent) => {
    event.preventDefault();
    const text = DOMPurify.sanitize(event.clipboardData.getData("text/plain"), {
      ALLOWED_TAGS: [],
      ALLOWED_ATTR: [],
    });
    document.execCommand("insertText", false, text);
  };

  const handleKeyDown = (event: React.KeyboardEvent) => {
    if (event.key === "Enter") {
      event.preventDefault();
      document.execCommand("insertText", false, "\n");
    }
  };

  return (
    <div className="flex flex-col gap-1.5">
      <div className="flex flex-wrap gap-1">
        {tokens.map((token) => (
          <button
            key={token.name}
            type="button"
            onClick={() => insertToken(token)}
            className={cn(PILL_CLASS, "cursor-pointer border border-border hover:bg-accent")}
          >
            {token.label}
          </button>
        ))}
      </div>
      {/* biome-ignore lint/a11y/useSemanticElements: a contentEditable token editor renders inline pills, so it cannot be a <textarea>. */}
      <div
        ref={ref}
        role="textbox"
        tabIndex={0}
        aria-multiline="true"
        aria-label={ariaLabel}
        contentEditable
        suppressContentEditableWarning
        onInput={emit}
        onPaste={handlePaste}
        onKeyDown={handleKeyDown}
        className="min-h-16 whitespace-pre-wrap rounded-md border border-border bg-background px-3 py-2 text-sm text-foreground focus:outline-none focus-visible:ring-2 focus-visible:ring-primary"
      />
    </div>
  );
}
