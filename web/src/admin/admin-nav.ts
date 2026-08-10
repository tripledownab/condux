// The platform admin tabs, kept as data so the tab bar stays declarative. labelKey is translated under
// the "admin.tabs" namespace. Spend + audit are the management-console additions (ADR-0027).
export type AdminTab = { href: string; labelKey: string };

export const ADMIN_TABS: AdminTab[] = [
  { href: "/admin/overview", labelKey: "overview" },
  { href: "/admin/organizations", labelKey: "organizations" },
  { href: "/admin/users", labelKey: "users" },
  { href: "/admin/spend", labelKey: "spend" },
  { href: "/admin/audit", labelKey: "audit" },
];
