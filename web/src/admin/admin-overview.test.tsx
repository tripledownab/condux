import { screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { renderWithIntl } from "@/src/test-utils/render-with-intl";

const useAdminOverviewMock = vi.fn();
vi.mock("@/src/api/generated/condux", () => ({ useAdminOverview: () => useAdminOverviewMock() }));

import { AdminOverview } from "./admin-overview";

afterEach(() => vi.clearAllMocks());

describe("AdminOverview", () => {
  it("renders the platform totals", () => {
    useAdminOverviewMock.mockReturnValue({
      isPending: false,
      isError: false,
      data: { data: { orgs: 3, users: 7, projects: 4, issues: 12 } },
    });
    renderWithIntl(<AdminOverview />);

    expect(screen.getByText("3")).toBeInTheDocument();
    expect(screen.getByText("7")).toBeInTheDocument();
    expect(screen.getByText("4")).toBeInTheDocument();
    expect(screen.getByText("12")).toBeInTheDocument();
  });

  it("shows a notice while loading", () => {
    useAdminOverviewMock.mockReturnValue({ isPending: true, isError: false });
    renderWithIntl(<AdminOverview />);

    expect(screen.getByText("Loading platform stats.")).toBeInTheDocument();
  });
});
