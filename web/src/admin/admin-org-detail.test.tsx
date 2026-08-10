import { screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { renderWithIntl } from "@/src/test-utils/render-with-intl";

const detailMock = vi.fn();
vi.mock("@/src/api/generated/condux", () => ({ useAdminOrgDetail: () => detailMock() }));

// Shallow-render the sections so the orchestrator test stays about layout, not each section's internals.
vi.mock("./admin-org-info-card", () => ({ AdminOrgInfoCard: () => <div>info-card</div> }));
vi.mock("./admin-org-billing", () => ({ AdminOrgBilling: () => <div>billing</div> }));
vi.mock("./admin-org-spend-meter", () => ({ AdminOrgSpendMeter: () => <div>spend-meter</div> }));
vi.mock("./admin-org-members", () => ({ AdminOrgMembers: () => <div>members</div> }));
vi.mock("./admin-impersonate-button", () => ({
  AdminImpersonateButton: () => <div>impersonate</div>,
}));

import { AdminOrgDetail } from "./admin-org-detail";

afterEach(() => vi.clearAllMocks());

describe("AdminOrgDetail", () => {
  it("shows a loading notice while pending", () => {
    detailMock.mockReturnValue({ isPending: true, isError: false });
    renderWithIntl(<AdminOrgDetail orgId={1} />);
    expect(screen.getByText("Loading organization.")).toBeInTheDocument();
  });

  it("renders every section once loaded", () => {
    detailMock.mockReturnValue({
      isPending: false,
      isError: false,
      data: {
        data: {
          id: 1,
          slug: "acme",
          name: "Acme",
          tier: 1,
          aiFixMode: 0,
          aiFixCostCapUsd: null,
        },
      },
    });
    renderWithIntl(<AdminOrgDetail orgId={1} />);

    for (const section of ["info-card", "billing", "spend-meter", "members", "impersonate"]) {
      expect(screen.getByText(section)).toBeInTheDocument();
    }
  });
});
