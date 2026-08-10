import { screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { renderWithIntl } from "@/src/test-utils/render-with-intl";

const useAdminListOrgsMock = vi.fn();
vi.mock("@/src/api/generated/condux", () => ({ useAdminListOrgs: () => useAdminListOrgsMock() }));

import { AdminOrgs } from "./admin-orgs";

afterEach(() => vi.clearAllMocks());

describe("AdminOrgs", () => {
  it("renders a row per org with its plan name and owner", () => {
    useAdminListOrgsMock.mockReturnValue({
      isPending: false,
      isError: false,
      data: {
        data: [
          {
            id: 1,
            slug: "acme",
            name: "Acme",
            tier: 2,
            createdAt: "2026-01-02T03:04:05Z",
            ownerEmail: "owner@acme.test",
            memberCount: 4,
            projectCount: 2,
          },
        ],
      },
    });
    renderWithIntl(<AdminOrgs />);

    expect(screen.getByText("owner@acme.test")).toBeInTheDocument();
    expect(screen.getByText("Business")).toBeInTheDocument(); // tier 2 -> Business
    // The name links to the org detail page.
    expect(screen.getByRole("link", { name: "Acme" })).toHaveAttribute(
      "href",
      "/admin/organizations/1",
    );
  });

  it("falls back to a placeholder when an org has no owner", () => {
    useAdminListOrgsMock.mockReturnValue({
      isPending: false,
      isError: false,
      data: {
        data: [
          {
            id: 9,
            slug: "orphan",
            name: "Orphan",
            tier: 0,
            createdAt: "2026-01-02T03:04:05Z",
            ownerEmail: null,
            memberCount: 0,
            projectCount: 0,
          },
        ],
      },
    });
    renderWithIntl(<AdminOrgs />);

    expect(screen.getByText("Unknown")).toBeInTheDocument();
  });

  it("shows an empty notice when there are no orgs", () => {
    useAdminListOrgsMock.mockReturnValue({ isPending: false, isError: false, data: { data: [] } });
    renderWithIntl(<AdminOrgs />);

    expect(screen.getByText("No organizations yet.")).toBeInTheDocument();
  });
});
