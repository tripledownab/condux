import type { ReactNode } from "react";

// A centered message panel for empty, loading and error states. Shared by the issue list and detail.
export function Notice({ children }: { children: ReactNode }) {
  return (
    <div className="rounded-lg border border-border bg-card p-8 text-center text-sm text-muted-foreground">
      {children}
    </div>
  );
}
