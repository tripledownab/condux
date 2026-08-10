import { getTranslations } from "next-intl/server";
import type { ReactNode } from "react";
import { PageContainer } from "@/src/components/page-container";
import { SettingsTabs } from "@/src/settings/settings-tabs";

// The settings shell: a title and the tab bar above every settings page (General / Members / Projects /
// Alerts / GitHub). Each tab is its own route so the browser back button and deep links work.
export default async function SettingsLayout({ children }: { children: ReactNode }) {
  const translate = await getTranslations("settings");
  return (
    <PageContainer>
      <h1 className="font-heading text-2xl font-semibold text-foreground">{translate("title")}</h1>
      <div className="mt-4">
        <SettingsTabs />
      </div>
      <div className="mt-6">{children}</div>
    </PageContainer>
  );
}
