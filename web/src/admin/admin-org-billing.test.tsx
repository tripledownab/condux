import { fireEvent, screen, within } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { renderWithIntl } from "@/src/test-utils/render-with-intl";

vi.mock("@tanstack/react-query", () => ({
  useQueryClient: () => ({ invalidateQueries: vi.fn() }),
}));

const statusMock = vi.fn();
const cancelMutate = vi.fn();
const changePlanMutate = vi.fn();
const portalMutate = vi.fn();
vi.mock("@/src/api/generated/condux", () => ({
  useAdminBillingStatus: () => statusMock(),
  useAdminBillingCancel: () => ({ mutate: cancelMutate, isPending: false, isError: false }),
  useAdminBillingChangePlan: () => ({ mutate: changePlanMutate, isPending: false, isError: false }),
  useAdminBillingPortal: () => ({ mutate: portalMutate, isPending: false, isError: false }),
  getAdminBillingStatusQueryKey: () => ["billing", 1],
  getAdminOrgDetailQueryKey: () => ["org", 1],
}));

import { AdminOrgBilling } from "./admin-org-billing";

afterEach(() => vi.clearAllMocks());

const enabled = {
  isPending: false,
  isError: false,
  data: {
    data: {
      tier: "Team",
      billingEnabled: true,
      hasSubscription: true,
      stripeCustomerId: "cus_x",
      stripeSubscriptionId: "sub_x",
      purchasableTiers: ["Team", "Business"],
    },
  },
};

describe("AdminOrgBilling", () => {
  it("shows a disabled notice when Stripe is off", () => {
    statusMock.mockReturnValue({
      isPending: false,
      isError: false,
      data: {
        data: { tier: "Free", billingEnabled: false, hasSubscription: false, purchasableTiers: [] },
      },
    });
    renderWithIntl(<AdminOrgBilling orgId={1} />);
    expect(screen.getByText("Billing is not configured on this deployment.")).toBeInTheDocument();
  });

  it("changes plan, cancels (after confirm) and opens the portal", async () => {
    statusMock.mockReturnValue(enabled);
    renderWithIntl(<AdminOrgBilling orgId={1} />);

    fireEvent.click(screen.getByRole("button", { name: "Apply" }));
    expect(changePlanMutate).toHaveBeenCalledWith(
      { orgId: 1, data: { tier: "Team" } },
      expect.anything(),
    );

    fireEvent.click(screen.getByRole("button", { name: "Open Stripe portal" }));
    expect(portalMutate).toHaveBeenCalledWith({ orgId: 1 }, expect.anything());

    fireEvent.click(screen.getByRole("button", { name: "Cancel subscription" }));
    const dialog = await screen.findByRole("dialog");
    fireEvent.click(within(dialog).getByRole("button", { name: "Cancel subscription" }));
    expect(cancelMutate).toHaveBeenCalledWith({ orgId: 1 }, expect.anything());
  });
});
