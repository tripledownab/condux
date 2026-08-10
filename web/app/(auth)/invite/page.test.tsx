import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ConduxApiError } from "@/src/api/fetcher";
import { renderWithIntl } from "@/src/test-utils/render-with-intl";

const replace = vi.fn();
vi.mock("next/navigation", () => ({ useRouter: () => ({ replace }) }));
vi.mock("@tanstack/react-query", () => ({
  useQueryClient: () => ({ invalidateQueries: vi.fn() }),
}));

const useMeMock = vi.fn();
const acceptMutate = vi.fn();
const useAcceptInviteMock = vi.fn();
const useLogoutMock = vi.fn();
vi.mock("@/src/api/generated/condux", () => ({
  useMe: () => useMeMock(),
  useAcceptInvite: () => useAcceptInviteMock(),
  useLogout: () => useLogoutMock(),
  getMeQueryKey: () => ["me"],
  getListMyOrgsQueryKey: () => ["orgs"],
}));

import InvitePage from "./page";

const idleAccept = {
  mutate: acceptMutate,
  isPending: false,
  isError: false,
  isSuccess: false,
  error: null,
};

function signedIn(email = "test@thing.se") {
  useMeMock.mockReturnValue({ isPending: false, isError: false, data: { data: { email } } });
}

beforeEach(() => {
  window.history.replaceState(null, "", "/invite?token=abc");
  useMeMock.mockReturnValue({ isPending: true });
  useAcceptInviteMock.mockReturnValue(idleAccept);
  useLogoutMock.mockReturnValue({ mutate: vi.fn() });
});

afterEach(() => {
  vi.clearAllMocks();
  window.history.replaceState(null, "", "/");
});

describe("InvitePage", () => {
  it("prompts a signed-out invitee to sign up or in, carrying the token through auth", () => {
    useMeMock.mockReturnValue({ isPending: false, isError: true });
    renderWithIntl(<InvitePage />);

    expect(screen.getByText(/invited to join a team on Condux/)).toBeInTheDocument();
    const next = encodeURIComponent("/invite?token=abc");
    expect(screen.getByRole("link", { name: "Create account" })).toHaveAttribute(
      "href",
      `/signup?next=${next}`,
    );
    expect(screen.getByRole("link", { name: "Sign in" })).toHaveAttribute(
      "href",
      `/login?next=${next}`,
    );
  });

  it("accepts the invitation for a signed-in user", async () => {
    signedIn();
    renderWithIntl(<InvitePage />);

    expect(screen.getByText("Signed in as test@thing.se.")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Accept invitation" }));
    expect(acceptMutate).toHaveBeenCalledWith(
      { data: { token: "abc" } },
      expect.objectContaining({ onSuccess: expect.any(Function) }),
    );
  });

  it("explains an email mismatch and offers to switch account (403)", () => {
    signedIn("someone.else@acme.io");
    useAcceptInviteMock.mockReturnValue({
      ...idleAccept,
      isError: true,
      error: new ConduxApiError("POST", "/invites/accept", 403, "forbidden"),
    });
    renderWithIntl(<InvitePage />);

    expect(screen.getByText(/different email address/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Use a different account" })).toBeInTheDocument();
  });

  it("shows an invalid-link message when the URL has no token", () => {
    window.history.replaceState(null, "", "/invite");
    useMeMock.mockReturnValue({ isPending: false, isError: true });
    renderWithIntl(<InvitePage />);

    expect(screen.getByText(/invitation link is invalid or incomplete/)).toBeInTheDocument();
  });

  it("confirms success after joining", async () => {
    signedIn();
    useAcceptInviteMock.mockReturnValue({ ...idleAccept, isSuccess: true });
    renderWithIntl(<InvitePage />);

    await waitFor(() => expect(screen.getByText(/You have joined the team/)).toBeInTheDocument());
  });
});
