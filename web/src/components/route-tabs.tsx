"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import { Tabs, TabsList, TabsTrigger } from "@/src/components/ui/tabs";

export type RouteTab = { href: string; label: string };

// A route-based tab bar (one page per tab): the shadcn Tabs styling driven by the current path rather
// than local state. A tab is active when the path is under its href, so a nested route keeps its parent
// tab highlighted. Each trigger is a Next Link (asChild), so navigation stays a real anchor. Shared by
// the settings and admin tab bars; callers resolve their own i18n labels.
export function RouteTabs({ tabs }: { tabs: RouteTab[] }) {
  const pathname = usePathname();
  const active = tabs.find((tab) => pathname.startsWith(tab.href))?.href ?? "";
  return (
    <Tabs value={active}>
      <TabsList>
        {tabs.map((tab) => (
          <TabsTrigger key={tab.href} value={tab.href} asChild>
            <Link href={tab.href}>{tab.label}</Link>
          </TabsTrigger>
        ))}
      </TabsList>
    </Tabs>
  );
}
