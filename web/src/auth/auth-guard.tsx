"use client";

import { useRouter } from "next/navigation";
import { type ReactNode, useEffect } from "react";
import { useMe } from "@/src/api/generated/condux";
import { FullScreenLoading } from "@/src/components/loading";
import { ROUTES } from "@/src/routes";

// Client-side gate for the authed app shell: it resolves the session via /api/auth/me. While that
// loads we show a spinner; a 401 (no or expired session) redirects to /login. This is UX only - every
// API call is still authorized server-side, which is the real security boundary.
export function AuthGuard({ children }: { children: ReactNode }) {
  const router = useRouter();
  const { isPending, isError } = useMe();

  useEffect(() => {
    if (isError) {
      router.replace(ROUTES.login);
    }
  }, [isError, router]);

  if (isPending) {
    return <FullScreenLoading />;
  }
  if (isError) {
    return null; // redirecting to /login
  }
  return <>{children}</>;
}
