import type { ReactNode } from "react";

// The scroll-and-padding wrapper for regular pages (settings, admin, onboarding). The app shell's
// main region is a bare fill container so full-bleed surfaces (the issues quad pane) can own their
// scrolling; everything else wraps its content in this.
export function PageContainer({ children }: { children: ReactNode }) {
  return <div className="h-full overflow-y-auto p-6">{children}</div>;
}
