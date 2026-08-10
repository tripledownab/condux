"use client";

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { ThemeProvider } from "next-themes";
import { type ReactNode, useState } from "react";
import { ReplayOnboardingProvider } from "@/src/onboarding/replay-onboarding";
import { SelectedOrgProvider } from "@/src/orgs/selected-org";
import { SelectedProjectProvider } from "@/src/projects/selected-project";

// Client-side context for the whole app: TanStack Query for data and next-themes for light/dark/system.
// One QueryClient is created per browser session (useState keeps it stable across re-renders). A 401
// from /api/auth/me means "logged out", which the auth guard handles - so queries must not retry it.
export function Providers({ children }: { children: ReactNode }) {
  const [queryClient] = useState(
    () =>
      new QueryClient({
        defaultOptions: { queries: { retry: false, refetchOnWindowFocus: false } },
      }),
  );

  return (
    <QueryClientProvider client={queryClient}>
      <ThemeProvider
        attribute="data-theme"
        defaultTheme="system"
        enableSystem
        disableTransitionOnChange
      >
        <ReplayOnboardingProvider>
          <SelectedOrgProvider>
            <SelectedProjectProvider>{children}</SelectedProjectProvider>
          </SelectedOrgProvider>
        </ReplayOnboardingProvider>
      </ThemeProvider>
    </QueryClientProvider>
  );
}
