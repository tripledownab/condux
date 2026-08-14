"use client";

import { ConduxErrorBoundary } from "@condux/nextjs/react";
import { useTranslations } from "next-intl";
import type { ReactNode } from "react";
import { ImpersonationBanner } from "@/src/admin/impersonation-banner";
import { OnboardingGate } from "@/src/onboarding/onboarding-gate";
import { ConduxEnrichment } from "./condux-enrichment";
import { Sidebar } from "./sidebar";

// The authed dashboard chrome: a fixed sidebar (nav + project scope + account, all in its base) and a
// bare main region — no top bar. The main is a fill container (no scroll, no padding) so full-bleed
// surfaces like the issues quad pane own their scrolling; regular pages wrap themselves in PageContainer.
// The OnboardingGate keeps a tenant-less user on /onboarding until they create their organization; the
// impersonation banner (ADR-0027) sits above the content whenever an admin is viewing as an org.
export function AppShell({ children }: { children: ReactNode }) {
  const translate = useTranslations("common");
  return (
    <div className="flex h-screen bg-background">
      <ConduxEnrichment />
      <Sidebar />
      <main className="flex min-w-0 flex-1 flex-col overflow-hidden">
        <ImpersonationBanner />
        <div className="min-h-0 flex-1 overflow-hidden">
          {/* Render errors never reach the global handlers, so the boundary (dogfooding the SDK's own
              @condux/nextjs/react surface) reports them and keeps the shell alive around the wreckage. */}
          <ConduxErrorBoundary
            fallback={
              <p className="p-6 text-sm text-muted-foreground">
                {translate("renderErrorFallback")}
              </p>
            }
          >
            <OnboardingGate>{children}</OnboardingGate>
          </ConduxErrorBoundary>
        </div>
      </main>
    </div>
  );
}
