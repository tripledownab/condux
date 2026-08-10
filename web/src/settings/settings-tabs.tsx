"use client";

import { useTranslations } from "next-intl";
import { RouteTabs } from "@/src/components/route-tabs";
import { SETTINGS_TABS } from "./settings-nav";

// The settings tab bar (shadcn Tabs over route Links via RouteTabs), rendered by the settings layout
// above every settings page.
export function SettingsTabs() {
  const translate = useTranslations("settings.tabs");
  const tabs = SETTINGS_TABS.map((tab) => ({ href: tab.href, label: translate(tab.labelKey) }));
  return <RouteTabs tabs={tabs} />;
}
