import { act, fireEvent, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ConduxApiError } from "@/src/api/fetcher";
import { renderWithIntl } from "@/src/test-utils/render-with-intl";

vi.mock("next/navigation", () => ({ usePathname: () => "/projects/proj-uuid" }));

const useListGithubInstallationsMock = vi.fn();
const useGithubConnectMock = vi.fn();
const useGithubHealthMock = vi.fn();
const useGithubSelectionMock = vi.fn();
const useGithubSelectMock = vi.fn();
const useDisconnectGithubMock = vi.fn();
vi.mock("@/src/api/generated/condux", () => ({
  useDisconnectGithub: () => useDisconnectGithubMock(),
  useListGithubInstallations: () => useListGithubInstallationsMock(),
  useGithubConnect: () => useGithubConnectMock(),
  useGithubHealth: () => useGithubHealthMock(),
  useGithubSelection: () => useGithubSelectionMock(),
  useGithubSelect: () => useGithubSelectMock(),
}));

// The org + role come from context; keep the real OrgStatus enum and fake only the resolved state.
const useCurrentOrgMock = vi.fn();
vi.mock("@/src/orgs/current-org", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/src/orgs/current-org")>()),
  useCurrentOrg: () => useCurrentOrgMock(),
}));

import { OrgStatus } from "@/src/orgs/current-org";
import { GitHubConnect } from "./github-connect";

const idle = { mutate: vi.fn(), isPending: false, isError: false, error: null };

const MANAGE_URL = "https://github.com/apps/condux-ai/installations/new";

const idleHealth = { data: undefined, isFetching: false, refetch: vi.fn() };

// The connect outcome arrives as a query param, so a test that exercises the return leg sets the URL.
function returnFromGithub(query: string) {
  window.history.replaceState(null, "", `/projects/proj-uuid${query}`);
}

beforeEach(() => {
  useGithubConnectMock.mockReturnValue(idle);
  useGithubHealthMock.mockReturnValue(idleHealth);
  useGithubSelectionMock.mockReturnValue({ data: undefined, isError: false });
  useGithubSelectMock.mockReturnValue(idle);
  useDisconnectGithubMock.mockReturnValue(idle);
  useCurrentOrgMock.mockReturnValue({ status: OrgStatus.Ready, org: { id: 1 }, role: "owner" });
});
afterEach(() => {
  returnFromGithub("");
  vi.clearAllMocks();
});

describe("GitHubConnect", () => {
  it("renders nothing until the org resolves", () => {
    useCurrentOrgMock.mockReturnValue({ status: OrgStatus.Loading });
    useListGithubInstallationsMock.mockReturnValue({ isPending: true, error: null });
    const { container } = renderWithIntl(<GitHubConnect />);
    expect(container).toBeEmptyDOMElement();
  });

  it("renders nothing when the GitHub App is not configured on the server (404)", () => {
    useListGithubInstallationsMock.mockReturnValue({
      isPending: false,
      error: new ConduxApiError("GET", "/github", 404, "not_found"),
    });
    const { container } = renderWithIntl(<GitHubConnect />);
    expect(container).toBeEmptyDOMElement();
  });

  it("offers Connect GitHub and starts the flow returning to the current page when not connected", () => {
    const mutate = vi.fn();
    useGithubConnectMock.mockReturnValue({ ...idle, mutate });
    useListGithubInstallationsMock.mockReturnValue({
      isPending: false,
      error: null,
      data: { status: 200, data: [] },
    });
    renderWithIntl(<GitHubConnect />);

    fireEvent.click(screen.getByRole("button", { name: "Connect GitHub" }));
    expect(mutate).toHaveBeenCalledWith(
      { orgId: 1, data: { returnPath: "/projects/proj-uuid" } },
      expect.objectContaining({ onSuccess: expect.any(Function) }),
    );
  });

  // The stored link and the real connection can disagree in both directions, so the check is offered in
  // both states and each outcome has to say what to do about it.
  it("verifies a live connection on demand and reports the account", () => {
    const refetch = vi.fn();
    useListGithubInstallationsMock.mockReturnValue({
      isPending: false,
      error: null,
      data: {
        status: 200,
        data: [
          {
            installationId: 7,
            accountLogin: "acme",
            createdAt: "2026-01-01",
            manageUrl: MANAGE_URL,
          },
        ],
      },
    });
    useGithubHealthMock.mockReturnValue({
      ...idleHealth,
      refetch,
      data: { status: 200, data: { status: "healthy", installationId: 7, accountLogin: "acme" } },
    });
    renderWithIntl(<GitHubConnect />);

    fireEvent.click(screen.getByRole("button", { name: "Check connection" }));
    expect(refetch).toHaveBeenCalled();
    expect(screen.getByText(/Connection verified/)).toBeTruthy();
  });

  // The case that motivated this: the row was lost locally while GitHub still holds the installation, so
  // the check has to point at reconnecting rather than leaving the user to guess.
  it("tells the user that reconnecting relinks an installation GitHub still holds", () => {
    useListGithubInstallationsMock.mockReturnValue({
      isPending: false,
      error: null,
      data: { status: 200, data: [] },
    });
    useGithubHealthMock.mockReturnValue({
      ...idleHealth,
      data: {
        status: 200,
        data: { status: "not_connected", installationId: null, accountLogin: null },
      },
    });
    renderWithIntl(<GitHubConnect />);

    expect(screen.getByText(/connecting again relinks it/)).toBeTruthy();
  });

  // A revoked install needs a reconnect and an unreachable GitHub needs a retry, so they must not read
  // the same: telling someone to reinstall over a transient outage wastes their time.
  it("separates a revoked installation from GitHub being unreachable", () => {
    useListGithubInstallationsMock.mockReturnValue({
      isPending: false,
      error: null,
      data: {
        status: 200,
        data: [
          {
            installationId: 7,
            accountLogin: "acme",
            createdAt: "2026-01-01",
            manageUrl: MANAGE_URL,
          },
        ],
      },
    });
    useGithubHealthMock.mockReturnValue({
      ...idleHealth,
      data: { status: 200, data: { status: "revoked", installationId: 7, accountLogin: "acme" } },
    });
    const { unmount } = renderWithIntl(<GitHubConnect />);
    expect(screen.getByText(/uninstalled or suspended/)).toBeTruthy();
    unmount();

    useGithubHealthMock.mockReturnValue({
      ...idleHealth,
      data: {
        status: 200,
        data: { status: "unreachable", installationId: 7, accountLogin: "acme" },
      },
    });
    renderWithIntl(<GitHubConnect />);
    expect(screen.getByText(/could not be reached/)).toBeTruthy();
  });

  it("shows the connected account and no button once installed", () => {
    useListGithubInstallationsMock.mockReturnValue({
      isPending: false,
      error: null,
      data: {
        status: 200,
        data: [{ installationId: 5, accountLogin: "acme", createdAt: "", manageUrl: MANAGE_URL }],
      },
    });
    renderWithIntl(<GitHubConnect />);
    expect(screen.getByText(/GitHub connected as acme/)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Connect GitHub" })).not.toBeInTheDocument();
  });

  // Changing repository access and uninstalling both live on GitHub, and there is no disconnect in Condux
  // yet, so the connected state has to say where to go rather than leaving people to hunt for it.
  it("links to the installation on GitHub once connected", () => {
    useListGithubInstallationsMock.mockReturnValue({
      isPending: false,
      error: null,
      data: {
        status: 200,
        data: [{ installationId: 5, accountLogin: "acme", createdAt: "", manageUrl: MANAGE_URL }],
      },
    });
    renderWithIntl(<GitHubConnect />);

    const link = screen.getByRole("link", { name: "Manage on GitHub" });
    expect(link).toHaveAttribute("href", MANAGE_URL);
    // Leaving the dashboard mid-flow would lose unsaved state, and noopener keeps the new tab from
    // reaching back into window.opener.
    expect(link).toHaveAttribute("target", "_blank");
    expect(link).toHaveAttribute("rel", "noopener noreferrer");
  });

  // The actions regressed onto separate rows once, because the check button was bundled in a column with
  // its own result message. Sharing a parent is what keeps them on one row, so assert that rather than CSS.
  it("keeps every action on a single row", () => {
    useListGithubInstallationsMock.mockReturnValue({
      isPending: false,
      error: null,
      data: {
        status: 200,
        data: [{ installationId: 5, accountLogin: "acme", createdAt: "", manageUrl: MANAGE_URL }],
      },
    });
    useGithubHealthMock.mockReturnValue({
      ...idleHealth,
      data: { status: 200, data: { status: "healthy", installationId: 5, accountLogin: "acme" } },
    });
    renderWithIntl(<GitHubConnect />);

    const check = screen.getByRole("button", { name: "Check connection" });
    const manage = screen.getByRole("link", { name: "Manage on GitHub" });
    expect(check.parentElement).toBe(manage.parentElement);
    // The verdict sits below the row, not inside it, which is what let them separate before.
    expect(screen.getByText(/Connection verified/).parentElement).not.toBe(check.parentElement);
  });

  // Disconnecting is reversible, but its consequences are not obvious (the app stays installed on
  // GitHub), so it confirms first and the confirmation has to say so.
  it("confirms before disconnecting and explains the app stays on GitHub", async () => {
    const mutate = vi.fn();
    const refetch = vi.fn();
    useDisconnectGithubMock.mockReturnValue({ ...idle, mutate });
    useListGithubInstallationsMock.mockReturnValue({
      isPending: false,
      error: null,
      refetch,
      data: {
        status: 200,
        data: [{ installationId: 5, accountLogin: "acme", createdAt: "", manageUrl: MANAGE_URL }],
      },
    });
    renderWithIntl(<GitHubConnect />);

    fireEvent.click(screen.getByRole("button", { name: "Disconnect" }));
    expect(mutate).not.toHaveBeenCalled();
    expect(screen.getByText(/stays installed on GitHub/)).toBeInTheDocument();

    fireEvent.click(screen.getAllByRole("button", { name: "Disconnect" })[1]);
    expect(mutate).toHaveBeenCalledWith(
      { orgId: 1 },
      expect.objectContaining({ onSuccess: expect.any(Function) }),
    );

    act(() => mutate.mock.calls[0][1].onSuccess());
    expect(refetch).toHaveBeenCalled();
  });

  it("can back out of disconnecting", () => {
    const mutate = vi.fn();
    useDisconnectGithubMock.mockReturnValue({ ...idle, mutate });
    useListGithubInstallationsMock.mockReturnValue({
      isPending: false,
      error: null,
      data: {
        status: 200,
        data: [{ installationId: 5, accountLogin: "acme", createdAt: "", manageUrl: MANAGE_URL }],
      },
    });
    renderWithIntl(<GitHubConnect />);

    fireEvent.click(screen.getByRole("button", { name: "Disconnect" }));
    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));
    expect(screen.queryByText(/stays installed on GitHub/)).not.toBeInTheDocument();
    expect(mutate).not.toHaveBeenCalled();
  });

  // Whoever cannot reconnect should not be able to disconnect either.
  it("hides Disconnect from a non-admin", () => {
    useCurrentOrgMock.mockReturnValue({ status: OrgStatus.Ready, org: { id: 1 }, role: "member" });
    useListGithubInstallationsMock.mockReturnValue({
      isPending: false,
      error: null,
      data: {
        status: 200,
        data: [{ installationId: 5, accountLogin: "acme", createdAt: "", manageUrl: MANAGE_URL }],
      },
    });
    renderWithIntl(<GitHubConnect />);
    expect(screen.queryByRole("button", { name: "Disconnect" })).not.toBeInTheDocument();
  });

  // The message that sent the user here has to say what to do, not just what went wrong.
  it("tells a blocked org how to resolve an installation held elsewhere", () => {
    useListGithubInstallationsMock.mockReturnValue({
      isPending: false,
      error: null,
      data: { status: 200, data: [] },
    });
    returnFromGithub("?github=taken");
    renderWithIntl(<GitHubConnect />);
    expect(screen.getByText(/Disconnect it there first/)).toBeInTheDocument();
  });

  // The link belongs to an installation, so an org without one has nothing to point at.
  it("offers no manage link when nothing is connected", () => {
    useListGithubInstallationsMock.mockReturnValue({
      isPending: false,
      error: null,
      data: { status: 200, data: [] },
    });
    renderWithIntl(<GitHubConnect />);
    expect(screen.queryByRole("link", { name: "Manage on GitHub" })).not.toBeInTheDocument();
  });

  // GitHub names the account separately from the link, so a row can be connected with no account yet.
  // That gets its own sentence rather than a placeholder standing in for a value we don't have.
  it("says connected without naming an account GitHub has not told us about", () => {
    useListGithubInstallationsMock.mockReturnValue({
      isPending: false,
      error: null,
      data: {
        status: 200,
        data: [{ installationId: 5, accountLogin: null, createdAt: "", manageUrl: MANAGE_URL }],
      },
    });
    renderWithIntl(<GitHubConnect />);

    expect(screen.getByText(/^GitHub connected\./)).toBeInTheDocument();
    expect(screen.queryByText(/connected as/)).not.toBeInTheDocument();
  });

  it("hides the connect button for a non-admin", () => {
    useCurrentOrgMock.mockReturnValue({ status: OrgStatus.Ready, org: { id: 1 }, role: "member" });
    useListGithubInstallationsMock.mockReturnValue({
      isPending: false,
      error: null,
      data: { status: 200, data: [] },
    });
    renderWithIntl(<GitHubConnect />);
    expect(screen.queryByRole("button", { name: "Connect GitHub" })).not.toBeInTheDocument();
    expect(screen.getByText("Only an organization admin can connect GitHub.")).toBeInTheDocument();
  });

  it("names the project in the connect prompt when given one", () => {
    useListGithubInstallationsMock.mockReturnValue({
      isPending: false,
      error: null,
      data: { status: 200, data: [] },
    });
    renderWithIntl(<GitHubConnect projectName="Checkout" />);
    expect(screen.getByText(/fix pull requests for Checkout/)).toBeInTheDocument();
  });

  // GitHub gives no way to say which account an org meant, so when the authorizing user reaches several
  // installations the callback hands the choice back and this panel has to finish the connect.
  it("lets the user pick which GitHub account to connect when several are reachable", () => {
    const mutate = vi.fn();
    const refetch = vi.fn();
    useListGithubInstallationsMock.mockReturnValue({
      isPending: false,
      error: null,
      data: { status: 200, data: [] },
      refetch,
    });
    useGithubSelectionMock.mockReturnValue({
      isError: false,
      data: {
        status: 200,
        data: {
          installations: [
            { installationId: 51, accountLogin: "acme" },
            { installationId: 52, accountLogin: "acme-labs" },
          ],
        },
      },
    });
    useGithubSelectMock.mockReturnValue({ ...idle, mutate });
    returnFromGithub("?github=select&selection=signed-token");
    renderWithIntl(<GitHubConnect />);

    fireEvent.click(screen.getByRole("button", { name: "acme-labs" }));
    expect(mutate).toHaveBeenCalledWith(
      { orgId: 1, data: { selection: "signed-token", installationId: 52 } },
      expect.objectContaining({ onSuccess: expect.any(Function) }),
    );

    // Linking happens server-side, so the panel only catches up once the connection is re-read.
    act(() => mutate.mock.calls[0][1].onSuccess());
    expect(refetch).toHaveBeenCalled();
    expect(window.location.search).toBe("");
  });

  // Moving an installation between tenants is refused server-side, so the panel has to explain the refusal
  // rather than silently look unconnected.
  it("reports an installation that already belongs to another organization", () => {
    useListGithubInstallationsMock.mockReturnValue({
      isPending: false,
      error: null,
      data: { status: 200, data: [] },
    });
    returnFromGithub("?github=taken");
    renderWithIntl(<GitHubConnect />);

    expect(screen.getByText(/already connected to another Condux organization/)).toBeTruthy();
  });

  it("points the connected user to pick a repository for the project", () => {
    useListGithubInstallationsMock.mockReturnValue({
      isPending: false,
      error: null,
      data: {
        status: 200,
        data: [{ installationId: 5, accountLogin: "acme", createdAt: "", manageUrl: MANAGE_URL }],
      },
    });
    renderWithIntl(<GitHubConnect projectName="Checkout" />);
    expect(screen.getByText(/choose a repository for Checkout/)).toBeInTheDocument();
  });
});
