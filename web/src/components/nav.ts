import {
  type RemixiconComponentType,
  RiCodeSSlashLine,
  RiErrorWarningLine,
  RiGitPullRequestLine,
  RiRocketLine,
  RiSettings3Line,
  RiShieldUserLine,
  RiStackLine,
} from "@remixicon/react";
import { ROUTES } from "@/src/routes";

// Dashboard navigation, kept as data so the sidebar is declarative. labelKey is translated under the
// "nav" namespace; icon renders beside the label and stands alone when the sidebar is collapsed.
// adminOnly items show only to platform admins (the sidebar filters them by /api/auth/me). fixBadge /
// newIssueBadge items carry the unviewed-fix / new-issue count badge (the Fixes / Issues sections).
export type NavItem = {
  href: string;
  labelKey: string;
  icon: RemixiconComponentType;
  adminOnly?: boolean;
  fixBadge?: boolean;
  newIssueBadge?: boolean;
};

export const NAV_ITEMS: NavItem[] = [
  { href: ROUTES.home, labelKey: "issues", icon: RiErrorWarningLine, newIssueBadge: true },
  { href: ROUTES.fixes, labelKey: "fixes", icon: RiGitPullRequestLine, fixBadge: true },
  { href: ROUTES.projects, labelKey: "projects", icon: RiStackLine },
  { href: ROUTES.developer, labelKey: "developer", icon: RiCodeSSlashLine },
  { href: ROUTES.settings, labelKey: "settings", icon: RiSettings3Line },
  { href: ROUTES.onboarding, labelKey: "onboarding", icon: RiRocketLine },
  { href: ROUTES.admin, labelKey: "admin", icon: RiShieldUserLine, adminOnly: true },
];

// A nav item is active when the path is nested under it (section pages). The Issues item lives at home
// ("/") but its detail pages are under "/issues/*", so it stays active across the whole issues surface;
// home alone can't use a bare prefix since every path starts with "/". Pure so the sidebar stays wiring.
export function isNavItemActive(href: string, pathname: string): boolean {
  if (href === ROUTES.home) {
    return pathname === ROUTES.home || pathname.startsWith("/issues");
  }
  return pathname.startsWith(href);
}

// Which nav items a viewer sees. During onboarding only "Get started" shows (the rest of the app has
// nothing to show without a tenant); once onboarded it drops out and the rest show. The admin console
// is always platform-admin only. Pure so the sidebar stays declarative wiring.
export function visibleNavItems(options: {
  isPlatformAdmin: boolean;
  showOnboardingOnly: boolean;
}): NavItem[] {
  return NAV_ITEMS.filter((item) => {
    if (item.adminOnly && !options.isPlatformAdmin) {
      return false;
    }
    const isOnboarding = item.href === ROUTES.onboarding;
    return options.showOnboardingOnly ? isOnboarding : !isOnboarding;
  });
}
