import { fireEvent, screen, within } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { renderWithIntl } from "@/src/test-utils/render-with-intl";

vi.mock("@tanstack/react-query", () => ({
  useQueryClient: () => ({ invalidateQueries: vi.fn() }),
}));

const listMock = vi.fn();
const updateRoleMutate = vi.fn();
const removeMutate = vi.fn();
vi.mock("@/src/api/generated/condux", () => ({
  useAdminListOrgMembers: () => listMock(),
  useAdminUpdateMemberRole: () => ({ mutate: updateRoleMutate, isPending: false }),
  useAdminRemoveMember: () => ({ mutate: removeMutate, isPending: false }),
  getAdminListOrgMembersQueryKey: () => ["members", 1],
  getAdminOrgDetailQueryKey: () => ["org", 1],
}));

import { AdminOrgMembers } from "./admin-org-members";

afterEach(() => vi.clearAllMocks());

const members = [{ userId: 20, email: "member@acme.test", role: "member", createdAt: "" }];

describe("AdminOrgMembers", () => {
  it("renders a role selector per member", () => {
    listMock.mockReturnValue({ isPending: false, isError: false, data: { data: members } });
    renderWithIntl(<AdminOrgMembers orgId={1} />);

    // The role control is a Combobox; its open/select flow is covered by combobox.test.tsx and the
    // role-change wiring by the backend AdminMemberMgmtTest, so here we just assert the selector renders
    // for the member.
    expect(screen.getByLabelText("Role")).toBeInTheDocument();
    expect(screen.getByText("member@acme.test")).toBeInTheDocument();
  });

  it("removes a member after confirming", async () => {
    listMock.mockReturnValue({ isPending: false, isError: false, data: { data: members } });
    renderWithIntl(<AdminOrgMembers orgId={1} />);

    fireEvent.click(screen.getByRole("button", { name: "Remove" }));
    const dialog = await screen.findByRole("dialog");
    fireEvent.click(within(dialog).getByRole("button", { name: "Remove" }));
    expect(removeMutate).toHaveBeenCalledWith({ orgId: 1, userId: 20 }, expect.anything());
  });

  it("shows an empty notice when there are no members", () => {
    listMock.mockReturnValue({ isPending: false, isError: false, data: { data: [] } });
    renderWithIntl(<AdminOrgMembers orgId={1} />);
    expect(screen.getByText("No members.")).toBeInTheDocument();
  });
});
