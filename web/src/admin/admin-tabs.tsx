"use client";

import { useTranslations } from "next-intl";
import { RouteTabs } from "@/src/components/route-tabs";
import { ADMIN_TABS } from "./admin-nav";

// The platform admin tab bar (shadcn Tabs over route Links via RouteTabs). Mirrors SettingsTabs.
export function AdminTabs() {
  const translate = useTranslations("admin.tabs");
  const tabs = ADMIN_TABS.map((tab) => ({ href: tab.href, label: translate(tab.labelKey) }));
  return <RouteTabs tabs={tabs} />;
}
