import { screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { renderWithIntl } from "@/src/test-utils/render-with-intl";

const removeMutate = vi.fn();

vi.mock("@tanstack/react-query", () => ({
  useQueryClient: () => ({ invalidateQueries: vi.fn() }),
}));

const useCurrentOrgMock = vi.fn();
vi.mock("@/src/orgs/current-org", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/src/orgs/current-org")>()),
  useCurrentOrg: () => useCurrentOrgMock(),
}));

const useListMembersMock = vi.fn();
const useMeMock = vi.fn();
const useListInvitesMock = vi.fn();
vi.mock("@/src/api/generated/condux", () => ({
  useListMembers: () => useListMembersMock(),
  useMe: () => useMeMock(),
  useUpdateMemberRole: () => ({ mutate: vi.fn(), isPending: false }),
  useRemoveMember: () => ({ mutate: removeMutate, isPending: false }),
  useCreateInvite: () => ({ mutate: vi.fn(), isPending: false, isError: false }),
  useListInvites: () => useListInvitesMock(),
  useRevokeInvite: () => ({ mutate: vi.fn(), isPending: false }),
  getListMembersQueryKey: () => ["members"],
  getListInvitesQueryKey: () => ["invites"],
}));

import { OrgStatus } from "@/src/orgs/current-org";
import { OrgMembers } from "./org-members";

const org = { id: 1, name: "Acme", slug: "acme", tier: 2, createdAt: "" };
const members = [
  { userId: 10, email: "owner@acme.test", role: "owner", createdAt: "" },
  { userId: 20, email: "member@acme.test", role: "member", createdAt: "" },
];

afterEach(() => vi.clearAllMocks());

describe("OrgMembers", () => {
  it("lets an owner manage members and shows the invite section", () => {
    useCurrentOrgMock.mockReturnValue({ status: OrgStatus.Ready, org, role: "owner" });
    useListMembersMock.mockReturnValue({
      isPending: false,
      isError: false,
      data: { data: members },
    });
    useMeMock.mockReturnValue({ data: { data: { id: 10, email: "owner@acme.test" } } });
    useListInvitesMock.mockReturnValue({ isPending: false, isError: false, data: { data: [] } });
    renderWithIntl(<OrgMembers />);

    expect(screen.getByText("owner@acme.test")).toBeInTheDocument();
    expect(screen.getByText("you")).toBeInTheDocument(); // the owner is viewing their own row
    // The other member is editable by an owner: a role select and a remove button.
    expect(screen.getByRole("button", { name: "Remove" })).toBeInTheDocument();
    // The invite section is visible to owners.
    expect(screen.getByRole("heading", { name: "Invite a member" })).toBeInTheDocument();
    expect(screen.getByPlaceholderText("teammate@example.com")).toBeInTheDocument();
  });

  it("shows members read-only and hides invites for a plain member", () => {
    useCurrentOrgMock.mockReturnValue({ status: OrgStatus.Ready, org, role: "member" });
    useListMembersMock.mockReturnValue({
      isPending: false,
      isError: false,
      data: { data: members },
    });
    useMeMock.mockReturnValue({ data: { data: { id: 20, email: "member@acme.test" } } });
    useListInvitesMock.mockReturnValue({ isPending: false, isError: false, data: { data: [] } });
    renderWithIntl(<OrgMembers />);

    expect(screen.getByText("owner@acme.test")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Remove" })).not.toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: "Invite a member" })).not.toBeInTheDocument();
  });

  // An owner looking at someone else's row, which is the only state where Remove is offered.
  const asOwner = () => {
    useCurrentOrgMock.mockReturnValue({ status: OrgStatus.Ready, org, role: "owner" });
    useListMembersMock.mockReturnValue({
      isPending: false,
      isError: false,
      data: { data: members },
    });
    useMeMock.mockReturnValue({ data: { data: { id: 10, email: "owner@acme.test" } } });
    useListInvitesMock.mockReturnValue({ isPending: false, isError: false, data: { data: [] } });
  };

  // The point of the dialog is that the FIRST click removes nobody, so the absence is asserted before
  // the confirmation. Without that half this would pass just as well if Remove still fired immediately.
  it("asks before removing a member, and only removes once confirmed", async () => {
    asOwner();
    renderWithIntl(<OrgMembers />);

    await userEvent.click(screen.getByRole("button", { name: "Remove" }));
    expect(removeMutate).not.toHaveBeenCalled();

    const dialog = await screen.findByRole("dialog");
    expect(dialog).toHaveTextContent("member@acme.test");

    await userEvent.click(within(dialog).getByRole("button", { name: "Remove" }));
    expect(removeMutate).toHaveBeenCalledTimes(1);
  });

  it("removes nobody when the confirmation is cancelled", async () => {
    asOwner();
    renderWithIntl(<OrgMembers />);

    await userEvent.click(screen.getByRole("button", { name: "Remove" }));
    const dialog = await screen.findByRole("dialog");
    await userEvent.click(within(dialog).getByRole("button", { name: "Cancel" }));

    expect(removeMutate).not.toHaveBeenCalled();
  });
});
