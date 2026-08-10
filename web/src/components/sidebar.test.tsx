import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { renderWithIntl } from "@/src/test-utils/render-with-intl";

// The session, the account menu (covered by its own suite) and the router are faked; the collapse
// interaction and localStorage persistence run for real.
const useMeMock = vi.fn();
vi.mock("@/src/api/generated/condux", () => ({ useMe: () => useMeMock() }));

vi.mock("./user-menu", () => ({
  UserMenu: ({ compact }: { compact?: boolean }) => (
    <div>{compact ? "user-menu-compact" : "user-menu"}</div>
  ),
}));

// The Fixes / new-issue badges fetch the current project + a count, and the event stream opens an
// EventSource; stub them so the sidebar suite stays focused (each has its own coverage).
vi.mock("@/src/fixes/fixes-nav-badge", () => ({ FixesNavBadge: () => null }));
vi.mock("@/src/issues/new-issues-nav-badge", () => ({ NewIssuesNavBadge: () => null }));
vi.mock("@/src/realtime/project-event-stream", () => ({ ProjectEventStream: () => null }));

// The project switcher fetches the current project + the org's projects; stub it (own suite covers it).
vi.mock("./project-switcher", () => ({
  ProjectSwitcher: ({ compact }: { compact?: boolean }) => (
    <div>{compact ? "project-switcher-compact" : "project-switcher"}</div>
  ),
}));

// The plan badge resolves the current org; stub it so the sidebar suite stays focused.
vi.mock("./plan-badge", () => ({
  PlanBadge: ({ collapsed }: { collapsed?: boolean }) => (
    <div>{collapsed ? "plan-badge-compact" : "plan-badge"}</div>
  ),
}));

// Onboarding state drives which nav shows; the hook has its own suite, so fake it per test.
const useOnboardingMock = vi.fn();
vi.mock("@/src/onboarding/use-onboarding", () => ({
  useOnboarding: () => useOnboardingMock(),
}));

vi.mock("next/navigation", () => ({ usePathname: () => "/" }));

import { Sidebar } from "./sidebar";

const wallyMe = { data: { id: 1, email: "wally@acme.dev", isPlatformAdmin: false } };

beforeEach(() => {
  useMeMock.mockReturnValue({ data: wallyMe });
  // Default to an onboarded user (the full app nav); onboarding-mode tests override this.
  useOnboardingMock.mockReturnValue({ isPending: false, isComplete: true });
});

afterEach(() => {
  vi.clearAllMocks();
  window.localStorage.clear();
});

describe("Sidebar", () => {
  it("shows labeled nav and the account card, hiding admin for non-admins", () => {
    renderWithIntl(<Sidebar />);

    expect(screen.getByRole("link", { name: "Issues" })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Fixes" })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Settings" })).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "Admin" })).not.toBeInTheDocument();
    expect(screen.getByText("user-menu")).toBeInTheDocument();
  });

  it("shows the admin item to platform admins", () => {
    useMeMock.mockReturnValue({
      data: { data: { id: 1, email: "wally@acme.dev", isPlatformAdmin: true } },
    });
    renderWithIntl(<Sidebar />);

    expect(screen.getByRole("link", { name: "Admin" })).toBeInTheDocument();
  });

  it("drops Get started from the nav once onboarded", () => {
    renderWithIntl(<Sidebar />);

    expect(screen.queryByRole("link", { name: "Get started" })).not.toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Issues" })).toBeInTheDocument();
    expect(screen.getByText("project-switcher")).toBeInTheDocument();
  });

  it("shows only Get started and hides the project switcher during onboarding", () => {
    useOnboardingMock.mockReturnValue({ isPending: false, isComplete: false });
    renderWithIntl(<Sidebar />);

    expect(screen.getByRole("link", { name: "Get started" })).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "Issues" })).not.toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "Settings" })).not.toBeInTheDocument();
    expect(screen.queryByText("project-switcher")).not.toBeInTheDocument();
  });

  it("collapses to the icon rail and persists the preference", async () => {
    renderWithIntl(<Sidebar />);

    await userEvent.click(screen.getByRole("button", { name: "Collapse" }));

    expect(window.localStorage.getItem("condux.sidebarCollapsed")).toBe("true");
    expect(screen.getByRole("button", { name: "Expand sidebar" })).toBeInTheDocument();
    // Labels survive only for assistive tech; the nav still links every page.
    expect(screen.getByRole("link", { name: "Issues" })).toBeInTheDocument();
    expect(screen.queryByText("user-menu")).not.toBeInTheDocument();
    expect(screen.getByText("user-menu-compact")).toBeInTheDocument();

    await userEvent.click(screen.getByRole("button", { name: "Expand sidebar" }));
    expect(window.localStorage.getItem("condux.sidebarCollapsed")).toBe("false");
    expect(screen.getByText("user-menu")).toBeInTheDocument();
  });

  it("restores a collapsed preference from storage on mount", () => {
    window.localStorage.setItem("condux.sidebarCollapsed", "true");
    renderWithIntl(<Sidebar />);

    expect(screen.getByRole("button", { name: "Expand sidebar" })).toBeInTheDocument();
  });
});
