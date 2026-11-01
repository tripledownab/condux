import { fireEvent, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { renderWithIntl } from "@/src/test-utils/render-with-intl";

vi.mock("next/navigation", () => ({ usePathname: () => "/projects/abc" }));
vi.mock("@tanstack/react-query", () => ({
  useQueryClient: () => ({ invalidateQueries: vi.fn() }),
}));

const statusMock = vi.fn();
const orgMock = vi.fn();
const connectMutate = vi.fn();
const disconnectMutate = vi.fn();
vi.mock("@/src/api/generated/condux", () => ({
  useGetLinearProject: () => statusMock(),
  useGetLinearConnection: () => orgMock(),
  useLinearConnect: () => ({ mutate: connectMutate, isPending: false, isError: false }),
  useDisconnectLinear: () => ({ mutate: disconnectMutate, isPending: false, isError: false }),
  useListLinearTeams: () => ({
    data: { status: 200, data: [{ id: "t1", key: "ENG", name: "Engineering" }] },
    isPending: false,
    isError: false,
  }),
  useSetLinearMapping: () => ({ mutate: vi.fn(), isPending: false, isError: false }),
  useDeleteLinearMapping: () => ({ mutate: vi.fn(), isPending: false }),
  getGetLinearProjectQueryKey: () => ["linear"],
  useListLinearLabels: () => ({
    data: { status: 200, data: [] },
    isPending: false,
    isError: false,
  }),
}));

import { LinearTab } from "./linear-tab";

function status(data: Record<string, unknown>) {
  return {
    isPending: false,
    refetch: vi.fn(),
    data: {
      status: 200,
      data: { connected: false, broken: false, workspaceName: null, mapping: null, ...data },
    },
  };
}

beforeEach(() => {
  connectMutate.mockClear();
  disconnectMutate.mockClear();
  orgMock.mockReturnValue({
    data: { status: 200, data: { mappedProjects: ["Backend", "Checkout"] } },
  });
});

afterEach(() => window.history.replaceState(null, "", "/"));

describe("LinearTab", () => {
  // The callback returns to whatever path connect named, and the page opens the tab that ?tab= names.
  it("starts the connect from this project and asks to come back to this tab", () => {
    statusMock.mockReturnValue(status({}));
    renderWithIntl(<LinearTab projectId={1} orgId={9} canManage />);

    fireEvent.click(screen.getByRole("button", { name: "Connect Linear" }));

    expect(connectMutate).toHaveBeenCalledWith(
      { orgId: 9, data: { returnPath: "/projects/abc?tab=linear" } },
      expect.anything(),
    );
    expect(screen.getByText(/workspace admin has to approve/)).toBeInTheDocument();
  });

  it("offers a member no connect button", () => {
    statusMock.mockReturnValue(status({}));
    renderWithIntl(<LinearTab projectId={1} orgId={9} canManage={false} />);
    expect(screen.queryByRole("button", { name: "Connect Linear" })).not.toBeInTheDocument();
    expect(
      screen.getByText(/An admin of this organization can connect Linear/),
    ).toBeInTheDocument();
  });

  it("asks to reconnect a connection Linear stopped accepting, and hides the team picker", () => {
    statusMock.mockReturnValue(status({ connected: true, broken: true, workspaceName: "Acme" }));
    renderWithIntl(<LinearTab projectId={1} orgId={9} canManage />);
    expect(screen.getByRole("button", { name: "Reconnect Linear" })).toBeInTheDocument();
    expect(screen.queryByText("Linear team for this project")).not.toBeInTheDocument();
  });

  it("reports a failed return from Linear once and drops it from the URL", () => {
    window.history.replaceState(null, "", "/projects/abc?tab=linear&linear=failed");
    statusMock.mockReturnValue(status({}));
    renderWithIntl(<LinearTab projectId={1} orgId={9} canManage />);
    expect(screen.getByText(/didn't finish connecting/)).toBeInTheDocument();
    expect(window.location.search).toBe("?tab=linear");
  });

  // The connection is the org's, so disconnecting from one project has to say what the others lose.
  it("names every project that loses its team before disconnecting", () => {
    statusMock.mockReturnValue(
      status({
        connected: true,
        workspaceName: "Acme",
        mapping: {
          teamId: "t1",
          teamName: "Engineering",
          needsAttention: false,
          autoCreate: false,
          labels: [],
        },
      }),
    );
    renderWithIntl(<LinearTab projectId={1} orgId={9} canManage />);

    fireEvent.click(screen.getByRole("button", { name: "Disconnect Linear" }));

    expect(screen.getByText("Backend")).toBeInTheDocument();
    expect(screen.getByText("Checkout")).toBeInTheDocument();
    expect(disconnectMutate).not.toHaveBeenCalled();
  });

  it("keeps saying the revoke did not land after the connection is gone", () => {
    const connected = status({
      connected: true,
      workspaceName: "Acme",
      mapping: {
        teamId: "t1",
        teamName: "Engineering",
        needsAttention: false,
        autoCreate: false,
        labels: [],
      },
    });
    statusMock.mockReturnValue(connected);
    disconnectMutate.mockImplementation((_vars, options) => {
      statusMock.mockReturnValue(status({}));
      options.onSuccess({ status: 200, data: { revoked: false } });
    });
    const view = renderWithIntl(<LinearTab projectId={1} orgId={9} canManage />);
    fireEvent.click(screen.getByRole("button", { name: "Disconnect Linear" }));
    // The first click swaps the button for the confirmation, whose own button is the one that disconnects.
    fireEvent.click(screen.getByRole("button", { name: "Disconnect Linear" }));
    view.rerender(<LinearTab projectId={1} orgId={9} canManage />);

    expect(screen.getByText(/didn't confirm the access was revoked/)).toBeInTheDocument();
  });

  it("shows where a mapped project's tickets go and flags a team Linear refused", () => {
    statusMock.mockReturnValue(
      status({
        connected: true,
        workspaceName: "Acme",
        mapping: {
          teamId: "t1",
          teamName: "Engineering",
          needsAttention: true,
          autoCreate: false,
          labels: [],
        },
      }),
    );
    renderWithIntl(<LinearTab projectId={1} orgId={9} canManage={false} />);
    expect(screen.getByText(/go to the Engineering team/)).toBeInTheDocument();
    expect(screen.getByText(/lost access to the team/)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Change team" })).not.toBeInTheDocument();
  });
});
