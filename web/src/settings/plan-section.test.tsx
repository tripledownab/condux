import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { BillingStatusResponse } from "@/src/api/generated/model";
import { renderWithIntl } from "@/src/test-utils/render-with-intl";

const mutate = vi.fn();
const portalOpen = vi.fn();
const billing = { data: undefined as BillingStatusResponse | undefined };
vi.mock("@/src/billing/use-billing", () => ({
  useBillingStatus: () => billing,
  useCheckout: () => ({ mutate, isPending: false, isError: false }),
  usePortal: () => ({ open: portalOpen, isPending: false, isError: false }),
  tierRank: (name: string) => ({ Free: 0, Team: 1, Business: 2, Enterprise: 3 })[name] ?? 0,
}));
vi.mock("@tanstack/react-query", () => ({
  useQueryClient: () => ({ invalidateQueries: vi.fn() }),
}));
vi.mock("@/src/api/generated/condux", () => ({ getListMyOrgsQueryKey: () => ["orgs"] }));

import { PlanSection } from "./plan-section";

afterEach(() => {
  vi.clearAllMocks();
  billing.data = undefined;
});

describe("PlanSection", () => {
  it("offers upgrades above the current tier and starts checkout on click", async () => {
    billing.data = {
      tier: "Free",
      billingEnabled: true,
      purchasableTiers: ["Team", "Business"],
      hasSubscription: false,
    };

    renderWithIntl(<PlanSection orgId={1} tier={0} canManage />);

    expect(screen.getByText("You are on the Free plan.")).toBeTruthy();
    await userEvent.click(screen.getByRole("button", { name: "Upgrade to Team" }));
    expect(mutate).toHaveBeenCalledWith("Team");
    expect(screen.getByRole("button", { name: "Upgrade to Business" })).toBeTruthy();
  });

  it("gives a subscriber the manage-subscription button (not upgrade checkout) and opens the portal", async () => {
    billing.data = {
      tier: "Team",
      billingEnabled: true,
      purchasableTiers: ["Team", "Business"],
      hasSubscription: true,
    };

    renderWithIntl(<PlanSection orgId={1} tier={1} canManage />);

    // A subscriber changes/downgrades/cancels via the portal, never a fresh checkout (which would create a
    // second subscription), so no upgrade buttons show.
    expect(screen.queryByRole("button", { name: /upgrade/i })).toBeNull();
    await userEvent.click(screen.getByRole("button", { name: "Manage subscription" }));
    expect(portalOpen).toHaveBeenCalled();
  });

  it("degrades to the static note when Stripe billing is not configured", () => {
    billing.data = {
      tier: "Free",
      billingEnabled: false,
      purchasableTiers: [],
      hasSubscription: false,
    };

    renderWithIntl(<PlanSection orgId={1} tier={0} canManage />);

    expect(screen.queryByRole("button", { name: /upgrade/i })).toBeNull();
    expect(screen.getByText(/see the pricing page/i)).toBeTruthy();
  });
});
