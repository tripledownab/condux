// The settings sub-navigation, as data so the tab bar is declarative. labelKey is translated under
// "settings.tabs". Ordered General, Members, Alerts, Notifications, SSO, AI provider. Projects moved to the
// top-level Projects section (#124); GitHub connection moved into each project's Repositories (#134).
export type SettingsTab = { href: string; labelKey: string };

export const SETTINGS_TABS: SettingsTab[] = [
  { href: "/settings/general", labelKey: "general" },
  { href: "/settings/members", labelKey: "members" },
  { href: "/settings/alerts", labelKey: "alerts" },
  { href: "/settings/notifications", labelKey: "notifications" },
  { href: "/settings/sso", labelKey: "sso" },
  { href: "/settings/provider", labelKey: "provider" },
];
