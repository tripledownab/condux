import { fireEvent, screen, within } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { renderWithIntl } from "@/src/test-utils/render-with-intl";

const usersMock = vi.fn();
vi.mock("@/src/api/generated/condux", () => ({ useAdminListUsers: () => usersMock() }));

const startMock = vi.fn();
vi.mock("./use-impersonate-org", () => ({
  useImpersonateOrg: () => ({ start: startMock, pending: false, isError: false }),
}));

import { AdminUsers } from "./admin-users";

afterEach(() => vi.clearAllMocks());

const users = [
  {
    id: 1,
    email: "member@acme.test",
    createdAt: "2026-01-02T03:04:05Z",
    orgCount: 1,
    orgId: 10,
    orgName: "Acme",
  },
  {
    id: 2,
    email: "orphan@x.test",
    createdAt: "2026-01-02T03:04:05Z",
    orgCount: 0,
    orgId: null,
    orgName: null,
  },
];

describe("AdminUsers", () => {
  it("shows each user's org and a view-as action only when they belong to one", () => {
    usersMock.mockReturnValue({ isPending: false, isError: false, data: { data: users } });
    renderWithIntl(<AdminUsers />);

    expect(screen.getByText("Acme")).toBeInTheDocument(); // the member's org
    expect(screen.getByText("—")).toBeInTheDocument(); // the orphan has no org
    // Exactly one view-as action (the member with an org); the orphan has none.
    expect(screen.getAllByRole("button", { name: "View as org" })).toHaveLength(1);
  });

  it("starts a view-as session over the user's org after confirming", async () => {
    usersMock.mockReturnValue({ isPending: false, isError: false, data: { data: users } });
    renderWithIntl(<AdminUsers />);

    fireEvent.click(screen.getByRole("button", { name: "View as org" }));
    const dialog = await screen.findByRole("dialog");
    fireEvent.click(within(dialog).getByRole("button", { name: "View as org" }));
    expect(startMock).toHaveBeenCalledWith(10); // the member's org id
  });

  it("shows an empty notice when there are no users", () => {
    usersMock.mockReturnValue({ isPending: false, isError: false, data: { data: [] } });
    renderWithIntl(<AdminUsers />);
    expect(screen.getByText("No users yet.")).toBeInTheDocument();
  });
});
