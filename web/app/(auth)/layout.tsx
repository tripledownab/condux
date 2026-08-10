import type { ReactNode } from "react";
import { Logo } from "@/src/components/logo";
import { ThemeToggle } from "@/src/components/theme-toggle";

// Public shell for the login and signup pages: no app chrome, just a centered card under the logo,
// with the theme toggle available before sign-in.
export default function AuthLayout({ children }: { children: ReactNode }) {
  return (
    <div className="flex min-h-screen flex-col bg-background">
      <div className="flex justify-end p-4">
        <ThemeToggle />
      </div>
      <main className="flex flex-1 items-center justify-center p-4">
        <div className="w-full max-w-sm">
          <Logo className="mx-auto mb-8 h-7 w-auto text-foreground" />
          {children}
        </div>
      </main>
    </div>
  );
}
