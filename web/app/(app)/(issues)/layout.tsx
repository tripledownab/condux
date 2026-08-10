import type { ReactNode } from "react";
import { IssuesSurface } from "@/src/issues/issues-surface";

// The issues route group: home (the select prompt) and the issue detail both render inside the
// quad-pane surface, so the views and issue-list rails stay put while the main pane routes.
export default function IssuesLayout({ children }: { children: ReactNode }) {
  return <IssuesSurface>{children}</IssuesSurface>;
}
