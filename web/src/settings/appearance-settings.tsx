"use client";

import { useTranslations } from "next-intl";
import { ThemeToggle } from "@/src/components/theme-toggle";

// The theme preference (light/dark/system), a per-device setting stored by next-themes. It lives in
// settings General (the shell has no top bar; nav, project scope, and account all live in the sidebar).
export function AppearanceSettings() {
  const translate = useTranslations("settings.appearance");
  return (
    <section>
      <h2 className="text-sm font-medium text-foreground">{translate("title")}</h2>
      <p className="mt-1 text-xs text-muted-foreground">{translate("description")}</p>
      <div className="mt-3">
        <ThemeToggle />
      </div>
    </section>
  );
}
