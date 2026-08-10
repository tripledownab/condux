"use client";

import { useTranslations } from "next-intl";
import { useTheme } from "next-themes";
import { useEffect, useState } from "react";
import { THEME_OPTIONS } from "@/src/theme/theme-options";

// A segmented light/dark/system control. next-themes resolves the value on the client only, so the
// active state is applied after mount to avoid a hydration mismatch (server and first client render
// both show nothing selected, then the effect fills it in).
export function ThemeToggle() {
  const translate = useTranslations("theme");
  const { theme, setTheme } = useTheme();
  const [mounted, setMounted] = useState(false);
  useEffect(() => setMounted(true), []);

  return (
    <fieldset className="inline-flex rounded-md border border-border bg-card p-0.5">
      <legend className="sr-only">{translate("label")}</legend>
      {THEME_OPTIONS.map((option) => {
        const active = mounted && theme === option;
        return (
          <button
            key={option}
            type="button"
            aria-pressed={active}
            onClick={() => setTheme(option)}
            className={`rounded px-2 py-1 text-xs transition-colors focus:outline-none focus-visible:ring-2 focus-visible:ring-primary ${
              active
                ? "bg-primary text-primary-foreground"
                : "text-muted-foreground hover:text-foreground"
            }`}
          >
            {translate(option)}
          </button>
        );
      })}
    </fieldset>
  );
}
