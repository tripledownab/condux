import { screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { renderWithIntl } from "@/src/test-utils/render-with-intl";

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
  useRemoveMember: () => ({ mutate: vi.fn(), isPending: false }),
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
});
