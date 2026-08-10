import type { ReactNode } from "react";
import { ImpersonationBanner } from "@/src/admin/impersonation-banner";
import { OnboardingGate } from "@/src/onboarding/onboarding-gate";
import { Sidebar } from "./sidebar";

// The authed dashboard chrome: a fixed sidebar (nav + project scope + account, all in its base) and a
// bare main region — no top bar. The main is a fill container (no scroll, no padding) so full-bleed
// surfaces like the issues quad pane own their scrolling; regular pages wrap themselves in PageContainer.
// The OnboardingGate keeps a tenant-less user on /onboarding until they create their organization; the
// impersonation banner (ADR-0027) sits above the content whenever an admin is viewing as an org.
export function AppShell({ children }: { children: ReactNode }) {
  return (
    <div className="flex h-screen bg-background">
      <Sidebar />
      <main className="flex min-w-0 flex-1 flex-col overflow-hidden">
        <ImpersonationBanner />
        <div className="min-h-0 flex-1 overflow-hidden">
          <OnboardingGate>{children}</OnboardingGate>
        </div>
      </main>
    </div>
  );
}
