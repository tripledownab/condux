"use client";

import { RiSidebarFoldLine, RiSidebarUnfoldLine } from "@remixicon/react";
import Link from "next/link";
import { usePathname } from "next/navigation";
import { useTranslations } from "next-intl";
import { useEffect, useState } from "react";
import { useMe } from "@/src/api/generated/condux";
import { FixesNavBadge } from "@/src/fixes/fixes-nav-badge";
import { NewIssuesNavBadge } from "@/src/issues/new-issues-nav-badge";
import { useOnboarding } from "@/src/onboarding/use-onboarding";
import { ProjectEventStream } from "@/src/realtime/project-event-stream";
import { ROUTES } from "@/src/routes";
import { Logo, LogoMark } from "./logo";
import { isNavItemActive, visibleNavItems } from "./nav";
import { PlanBadge } from "./plan-badge";
import { ProjectSwitcher } from "./project-switcher";
import { UserMenu } from "./user-menu";

const COLLAPSED_STORAGE_KEY = "condux.sidebarCollapsed";

// Fixed left navigation for the authed shell, collapsible to an icon rail. Expanded: the logotype,
// the icon+label nav, then the collapse toggle above the user card (the account menu, layout 30's
// bottom card) pinned at the base. Collapsed: the square mark, icon-only nav, the expand toggle and
// the avatar-only account menu. The preference persists in localStorage like the project selection;
// it starts expanded so the server and first client render match, then hydrates from storage on
// mount. adminOnly items (the platform admin console) show only to platform admins.
export function Sidebar() {
  const translate = useTranslations("nav");
  const pathname = usePathname();
  const me = useMe().data?.data;
  const onboarding = useOnboarding();
  const [collapsed, setCollapsed] = useState(false);

  useEffect(() => {
    setCollapsed(window.localStorage.getItem(COLLAPSED_STORAGE_KEY) === "true");
  }, []);

  const toggleCollapsed = () => {
    const next = !collapsed;
    setCollapsed(next);
    window.localStorage.setItem(COLLAPSED_STORAGE_KEY, String(next));
  };

  // Until onboarding resolves as complete the nav collapses to just "Get started" and the project
  // switcher hides (there is no org/project yet). Optimistic while membership is still loading, so the
  // common (onboarded) case does not flash: only collapse once we know onboarding is incomplete.
  const showOnboardingOnly = !onboarding.isPending && !onboarding.isComplete;
  const items = visibleNavItems({
    isPlatformAdmin: me?.isPlatformAdmin ?? false,
    showOnboardingOnly,
  });

  if (collapsed) {
    return (
      <aside className="flex w-14 shrink-0 flex-col items-center border-r border-border bg-card">
        <ProjectEventStream />
        <div className="flex h-14 w-full items-center justify-center border-b border-border">
          <Link href={ROUTES.home} aria-label={translate("home")}>
            <LogoMark className="size-8 text-foreground" />
          </Link>
        </div>
        <nav aria-label="Primary" className="flex w-full flex-1 flex-col gap-1 p-2">
          {items.map((item) => {
            const active = isNavItemActive(item.href, pathname);
            return (
              <Link
                key={item.href}
                href={item.href}
                title={translate(item.labelKey)}
                aria-current={active ? "page" : undefined}
                className={`relative flex justify-center rounded-md p-2 transition-colors ${
                  active
                    ? "bg-secondary text-foreground"
                    : "text-muted-foreground hover:bg-secondary hover:text-foreground"
                }`}
              >
                <item.icon className="size-5" aria-hidden="true" />
                {item.fixBadge ? <FixesNavBadge collapsed /> : null}
                {item.newIssueBadge ? <NewIssuesNavBadge collapsed /> : null}
                <span className="sr-only">{translate(item.labelKey)}</span>
              </Link>
            );
          })}
        </nav>
        <button
          type="button"
          title={translate("expand")}
          onClick={toggleCollapsed}
          className="rounded-md p-2 text-muted-foreground transition-colors hover:bg-secondary hover:text-foreground"
        >
          <RiSidebarUnfoldLine className="size-5" aria-hidden="true" />
          <span className="sr-only">{translate("expand")}</span>
        </button>
        {showOnboardingOnly ? null : (
          <div className="mt-2">
            <PlanBadge collapsed />
          </div>
        )}
        {showOnboardingOnly ? null : (
          <div className="mt-2">
            <ProjectSwitcher compact />
          </div>
        )}
        <div className="mt-2 mb-3">
          <UserMenu compact />
        </div>
      </aside>
    );
  }

  return (
    <aside className="flex w-60 shrink-0 flex-col border-r border-border bg-card">
      <ProjectEventStream />
      <div className="flex h-14 items-center border-b border-border px-4">
        <Link href={ROUTES.home} aria-label={translate("home")}>
          <Logo className="h-5 w-auto text-foreground" />
        </Link>
      </div>
      <nav aria-label="Primary" className="flex flex-1 flex-col gap-1 p-3">
        {items.map((item) => {
          const active = isNavItemActive(item.href, pathname);
          return (
            <Link
              key={item.href}
              href={item.href}
              aria-current={active ? "page" : undefined}
              className={`flex items-center gap-2.5 rounded-md px-3 py-2 text-sm transition-colors ${
                active
                  ? "bg-secondary text-foreground"
                  : "text-muted-foreground hover:bg-secondary hover:text-foreground"
              }`}
            >
              <item.icon className="size-4.5 shrink-0" aria-hidden="true" />
              <span>{translate(item.labelKey)}</span>
              {item.fixBadge ? <FixesNavBadge collapsed={false} /> : null}
              {item.newIssueBadge ? <NewIssuesNavBadge collapsed={false} /> : null}
            </Link>
          );
        })}
      </nav>
      <div className="p-3">
        <button
          type="button"
          onClick={toggleCollapsed}
          className="mb-2 flex w-full items-center gap-2 rounded-md px-2 py-1.5 text-xs text-muted-foreground transition-colors hover:bg-secondary hover:text-foreground"
        >
          <RiSidebarFoldLine className="size-4" aria-hidden="true" />
          <span>{translate("collapse")}</span>
        </button>
        {showOnboardingOnly ? null : <PlanBadge collapsed={false} />}
        {showOnboardingOnly ? null : (
          <div className="mb-2">
            <ProjectSwitcher />
          </div>
        )}
        <UserMenu />
      </div>
    </aside>
  );
}
