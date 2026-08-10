import type { ReactNode } from "react";
import { FixesSurface } from "@/src/fixes/fixes-surface";

// The fixes route group: the select prompt and the fix detail both render inside the tri-pane
// surface, so the views and fix-list rails stay put while the main pane routes.
export default function FixesLayout({ children }: { children: ReactNode }) {
  return <FixesSurface>{children}</FixesSurface>;
}
