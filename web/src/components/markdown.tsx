import ReactMarkdown from "react-markdown";

// Renders LLM-authored markdown (Conductor fix summaries) safely: react-markdown escapes HTML by
// default, so no raw HTML from the model is injected. Styled to our tokens; the subset the Conductor
// emits is paragraphs, inline code, emphasis, lists and links.
export function Markdown({ children }: { children: string }) {
  return (
    <div className="flex flex-col gap-2 text-sm text-foreground">
      <ReactMarkdown
        components={{
          p: ({ children }) => <p className="leading-relaxed">{children}</p>,
          a: ({ href, children }) => (
            <a
              href={href}
              target="_blank"
              rel="noopener noreferrer"
              className="text-primary hover:underline"
            >
              {children}
            </a>
          ),
          code: ({ children }) => (
            <code className="rounded bg-secondary px-1 py-0.5 font-mono text-[0.85em] text-foreground">
              {children}
            </code>
          ),
          pre: ({ children }) => (
            <pre className="overflow-x-auto rounded-md border border-border bg-card p-3 text-xs">
              {children}
            </pre>
          ),
          ul: ({ children }) => <ul className="ml-4 flex list-disc flex-col gap-1">{children}</ul>,
          ol: ({ children }) => (
            <ol className="ml-4 flex list-decimal flex-col gap-1">{children}</ol>
          ),
          li: ({ children }) => <li className="leading-relaxed">{children}</li>,
          strong: ({ children }) => <strong className="font-semibold">{children}</strong>,
          h1: ({ children }) => (
            <h3 className="font-heading text-base font-semibold">{children}</h3>
          ),
          h2: ({ children }) => (
            <h3 className="font-heading text-base font-semibold">{children}</h3>
          ),
          h3: ({ children }) => <h3 className="font-heading text-sm font-semibold">{children}</h3>,
        }}
      >
        {children}
      </ReactMarkdown>
    </div>
  );
}
