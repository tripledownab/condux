"use client";

import { useRouter } from "next/navigation";
import { type ReactNode, useEffect } from "react";
import { useMe } from "@/src/api/generated/condux";
import { FullScreenLoading } from "@/src/components/loading";
import { ROUTES } from "@/src/routes";

// Client gate for the /admin area: only platform admins (isPlatformAdmin on /api/auth/me) may enter,
// anyone else is sent home. UX only - the /api/admin endpoints enforce the same gate server-side (a
// non-admin gets 404), which is the real security boundary.
export function AdminGuard({ children }: { children: ReactNode }) {
  const router = useRouter();
  const { data, isPending, isError } = useMe();
  const isPlatformAdmin = data?.data?.isPlatformAdmin ?? false;

  useEffect(() => {
    if (isError || (!isPending && !isPlatformAdmin)) {
      router.replace(ROUTES.home);
    }
  }, [isError, isPending, isPlatformAdmin, router]);

  if (isPending) {
    return <FullScreenLoading />;
  }
  if (isError || !isPlatformAdmin) {
    return null; // redirecting home
  }
  return <>{children}</>;
}
