import type { ReactNode } from "react";
import { AuthGuard } from "@/src/auth/auth-guard";
import { AppShell } from "@/src/components/app-shell";

// Every route in this group is gated by the session and wrapped in the app chrome.
export default function AppLayout({ children }: { children: ReactNode }) {
  return (
    <AuthGuard>
      <AppShell>{children}</AppShell>
    </AuthGuard>
  );
}
