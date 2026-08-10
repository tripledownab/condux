import { screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { OrgStatus } from "@/src/orgs/current-org";
import { renderWithIntl } from "@/src/test-utils/render-with-intl";

const useCurrentOrgMock = vi.fn();
vi.mock("@/src/orgs/current-org", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/src/orgs/current-org")>()),
  useCurrentOrg: () => useCurrentOrgMock(),
}));

import { PlanBadge } from "./plan-badge";

afterEach(() => vi.clearAllMocks());

describe("PlanBadge", () => {
  it("renders nothing until an org resolves", () => {
    useCurrentOrgMock.mockReturnValue({ status: OrgStatus.Loading });

    const { container } = renderWithIntl(<PlanBadge collapsed={false} />);

    expect(container.firstChild).toBeNull();
  });

  it("shows the tier name and links to settings when expanded", () => {
    useCurrentOrgMock.mockReturnValue({
      status: OrgStatus.Ready,
      org: { id: 1, tier: 2 }, // Business
      role: "owner",
    });

    renderWithIntl(<PlanBadge collapsed={false} />);

    const link = screen.getByRole("link", { name: /business/i });
    expect(link.getAttribute("href")).toBe("/settings/general");
  });

  it("shows the tier initial and links to settings when collapsed", () => {
    useCurrentOrgMock.mockReturnValue({
      status: OrgStatus.Ready,
      org: { id: 1, tier: 1 }, // Team
      role: "member",
    });

    renderWithIntl(<PlanBadge collapsed />);

    const link = screen.getByRole("link");
    expect(link.getAttribute("href")).toBe("/settings/general");
    expect(link.textContent).toContain("T");
  });
});
