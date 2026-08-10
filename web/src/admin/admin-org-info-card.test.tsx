import { fireEvent, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { renderWithIntl } from "@/src/test-utils/render-with-intl";

vi.mock("@tanstack/react-query", () => ({
  useQueryClient: () => ({ invalidateQueries: vi.fn() }),
}));

const updateMutate = vi.fn();
vi.mock("@/src/api/generated/condux", () => ({
  useAdminUpdateOrg: () => ({ mutate: updateMutate, isPending: false, isError: false }),
  getAdminOrgDetailQueryKey: () => ["org", 1],
  getAdminListOrgsQueryKey: () => ["orgs"],
}));

import { AdminOrgInfoCard } from "./admin-org-info-card";

afterEach(() => vi.clearAllMocks());

const props = {
  orgId: 1,
  name: "Acme",
  slug: "acme",
  tier: 1,
  aiFixMode: 0,
  aiFixCostCapUsd: 25 as number | null,
};

describe("AdminOrgInfoCard", () => {
  it("saves an edited name, preserving the saved mode and cap", () => {
    renderWithIntl(<AdminOrgInfoCard {...props} />);
    fireEvent.change(screen.getByDisplayValue("Acme"), { target: { value: "Renamed" } });
    fireEvent.click(screen.getAllByRole("button", { name: "Save" })[0]);

    expect(updateMutate).toHaveBeenCalledWith(
      { orgId: 1, data: { name: "Renamed", aiFixMode: 0, aiFixCostCapUsd: 25 } },
      expect.anything(),
    );
  });

  it("toggling mode keeps the saved name and cap", () => {
    renderWithIntl(<AdminOrgInfoCard {...props} />);
    fireEvent.click(screen.getByLabelText("Automatic"));

    expect(updateMutate).toHaveBeenCalledWith(
      { orgId: 1, data: { name: "Acme", aiFixMode: 1, aiFixCostCapUsd: 25 } },
      expect.anything(),
    );
  });
});
