import { screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { renderWithIntl } from "@/src/test-utils/render-with-intl";

const spendMock = vi.fn();
vi.mock("@/src/api/generated/condux", () => ({ useAdminOrgSpend: () => spendMock() }));

import { AdminOrgSpendMeter } from "./admin-org-spend-meter";

afterEach(() => vi.clearAllMocks());

describe("AdminOrgSpendMeter", () => {
  it("shows spend against a cap when one is set", () => {
    spendMock.mockReturnValue({ data: { data: { totalUsd: 12.5, byModel: [], byOrg: [] } } });
    renderWithIntl(<AdminOrgSpendMeter orgId={1} capUsd={100} />);
    expect(screen.getByText("$12.50 of $100.00 cap")).toBeInTheDocument();
  });

  it("shows spend alone when there is no cap", () => {
    spendMock.mockReturnValue({ data: { data: { totalUsd: 12.5, byModel: [], byOrg: [] } } });
    renderWithIntl(<AdminOrgSpendMeter orgId={1} capUsd={null} />);
    expect(screen.getByText("$12.50 spent")).toBeInTheDocument();
  });
});
