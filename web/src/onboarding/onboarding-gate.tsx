"use client";

import { usePathname, useRouter } from "next/navigation";
import { type ReactNode, useEffect } from "react";
import { FullScreenLoading } from "@/src/components/loading";
import { ROUTES } from "@/src/routes";
import { useOnboarding } from "./use-onboarding";

// Access gate for the authed shell: until onboarding is complete (the user belongs to an organization),
// every route but /onboarding redirects there, so a new user never lands on an app that has nothing to
// show. The onboarding page itself always renders — it is the destination. Pairs with the sidebar, which
// shows only "Get started" until this gate opens. UX only: tenancy is still enforced server-side.
export function OnboardingGate({ children }: { children: ReactNode }) {
  const router = useRouter();
  const pathname = usePathname();
  const { isPending, isComplete } = useOnboarding();
  const onOnboarding = pathname === ROUTES.onboarding;

  useEffect(() => {
    if (!isPending && !isComplete && !onOnboarding) {
      router.replace(ROUTES.onboarding);
    }
  }, [isPending, isComplete, onOnboarding, router]);

  if (onOnboarding) {
    return <>{children}</>;
  }
  if (isPending) {
    return <FullScreenLoading />;
  }
  if (!isComplete) {
    return null; // redirecting to /onboarding
  }
  return <>{children}</>;
}
