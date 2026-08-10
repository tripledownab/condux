import { describe, expect, it } from "vitest";
import { ROUTES } from "@/src/routes";
import { isNavItemActive, visibleNavItems } from "./nav";

describe("isNavItemActive", () => {
  it("marks home (Issues) active on the issues surface and its detail pages", () => {
    expect(isNavItemActive("/", "/")).toBe(true);
    expect(isNavItemActive("/", "/issues/019fc1d6-6799-7191-ba3c-e9f8be552582")).toBe(true);
    expect(isNavItemActive("/", "/settings")).toBe(false);
    expect(isNavItemActive("/", "/projects")).toBe(false);
  });

  it("marks a section active when the path is nested under it", () => {
    expect(isNavItemActive("/settings", "/settings")).toBe(true);
    expect(isNavItemActive("/settings", "/settings/keys")).toBe(true);
    expect(isNavItemActive("/settings", "/")).toBe(false);
  });
});

describe("visibleNavItems", () => {
  it("shows only Get started during onboarding", () => {
    const items = visibleNavItems({ isPlatformAdmin: false, showOnboardingOnly: true });
    expect(items.map((item) => item.href)).toEqual([ROUTES.onboarding]);
  });

  it("shows only Get started during onboarding even for a platform admin", () => {
    const items = visibleNavItems({ isPlatformAdmin: true, showOnboardingOnly: true });
    expect(items.map((item) => item.href)).toEqual([ROUTES.onboarding]);
  });

  it("drops Get started once onboarded and keeps the app nav", () => {
    const hrefs = visibleNavItems({ isPlatformAdmin: false, showOnboardingOnly: false }).map(
      (item) => item.href,
    );
    expect(hrefs).not.toContain(ROUTES.onboarding);
    expect(hrefs).toContain(ROUTES.home);
    expect(hrefs).toContain(ROUTES.projects);
    expect(hrefs).toContain(ROUTES.settings);
    // The admin console stays hidden from a non-admin.
    expect(hrefs).not.toContain(ROUTES.admin);
  });

  it("includes the admin console for a platform admin once onboarded", () => {
    const hrefs = visibleNavItems({ isPlatformAdmin: true, showOnboardingOnly: false }).map(
      (item) => item.href,
    );
    expect(hrefs).toContain(ROUTES.admin);
    expect(hrefs).not.toContain(ROUTES.onboarding);
  });
});
