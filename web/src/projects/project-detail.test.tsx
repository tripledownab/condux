import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { OrgStatus } from "@/src/orgs/current-org";
import { renderWithIntl } from "@/src/test-utils/render-with-intl";

vi.mock("@tanstack/react-query", () => ({
  useQueryClient: () => ({ invalidateQueries: vi.fn() }),
}));

vi.mock("next/navigation", () => ({ useRouter: () => ({ push: vi.fn() }) }));

const useGetProjectMock = vi.fn();
const useListKeysMock = vi.fn();
vi.mock("@/src/api/generated/condux", () => ({
  useGetProject: () => useGetProjectMock(),
  useUpdateProject: () => ({ mutate: vi.fn(), isPending: false }),
  useDeleteProject: () => ({ mutate: vi.fn(), isPending: false }),
  getGetProjectQueryKey: () => ["project"],
  useListKeys: () => useListKeysMock(),
  useCreateKey: () => ({ mutate: vi.fn(), isPending: false }),
  useUpdateKey: () => ({ mutate: vi.fn(), isPending: false }),
  useRevokeKey: () => ({ mutate: vi.fn(), isPending: false }),
  getListKeysQueryKey: () => ["keys"],
}));

// The embedded Repositories + Releases sections have their own tests; stub them here so this test stays
// focused on the project overview + the tab shell and does not need their repo/release hooks.
vi.mock("@/src/settings/repositories", () => ({ Repositories: () => null }));
vi.mock("@/src/settings/releases", () => ({ Releases: () => null }));
vi.mock("@/src/settings/release-tokens", () => ({ ReleaseTokens: () => null }));

// The Releases tab reads the org's GitHub connection to decide between the record form and a connect
// prompt; drive that state directly rather than the installations query.
const useGithubConnectionMock = vi.fn();
vi.mock("@/src/settings/use-github-connection", () => ({
  useGithubConnection: () => useGithubConnectionMock(),
}));

const useCurrentOrgMock = vi.fn();
vi.mock("@/src/orgs/current-org", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/src/orgs/current-org")>()),
  useCurrentOrg: () => useCurrentOrgMock(),
}));

import { ProjectDetail } from "./project-detail";

const found = {
  data: {
    status: 200,
    data: {
      id: 7,
      publicId: "uuid-backend",
      orgId: 1,
      name: "Backend",
      platform: "python",
      createdAt: "2026-01-01T00:00:00Z",
    },
  },
  isPending: false,
  isError: false,
};

function asRole(role: string) {
  useCurrentOrgMock.mockReturnValue({ status: OrgStatus.Ready, org: { id: 1 }, role });
}

beforeEach(() => {
  asRole("admin");
  useGetProjectMock.mockReturnValue(found);
  // The embedded DsnKeys just needs to render; a pending key list shows its loading notice.
  useListKeysMock.mockReturnValue({ isPending: true, isError: false, data: undefined });
  useGithubConnectionMock.mockReturnValue({
    orgId: 1,
    loading: false,
    notConfigured: false,
    connected: true,
    canManage: true,
    account: "acme",
  });
});

afterEach(() => vi.clearAllMocks());

describe("ProjectDetail", () => {
  it("renders the project overview", () => {
    renderWithIntl(<ProjectDetail publicId="uuid-backend" />);
    expect(screen.getByRole("heading", { name: "Backend" })).toBeInTheDocument();
    expect(screen.getByText("Python")).toBeInTheDocument(); // platform value → display label
  });

  it("shows a not-found notice when the project cannot be loaded", () => {
    useGetProjectMock.mockReturnValue({ isPending: false, isError: true, data: undefined });
    renderWithIntl(<ProjectDetail publicId="uuid-missing" />);
    expect(screen.getByText(/could not be found/)).toBeInTheDocument();
  });

  it("shows Rename and Delete for an admin", () => {
    renderWithIntl(<ProjectDetail publicId="uuid-backend" />);
    expect(screen.getByRole("button", { name: "Rename" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Delete" })).toBeInTheDocument();
  });

  it("hides Rename and Delete from a member", () => {
    asRole("member");
    renderWithIntl(<ProjectDetail publicId="uuid-backend" />);
    expect(screen.queryByRole("button", { name: "Rename" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Delete" })).not.toBeInTheDocument();
  });

  it("shows the GitHub, Releases and DSN keys tabs", () => {
    renderWithIntl(<ProjectDetail publicId="uuid-backend" />);
    expect(screen.getByRole("tab", { name: "GitHub" })).toBeInTheDocument();
    expect(screen.getByRole("tab", { name: "Releases" })).toBeInTheDocument();
    expect(screen.getByRole("tab", { name: "DSN keys" })).toBeInTheDocument();
  });

  it("prompts to connect GitHub on the Releases tab when GitHub is not connected", async () => {
    useGithubConnectionMock.mockReturnValue({
      orgId: 1,
      loading: false,
      notConfigured: false,
      connected: false,
      canManage: true,
      account: null,
    });
    renderWithIntl(<ProjectDetail publicId="uuid-backend" />);

    await userEvent.click(screen.getByRole("tab", { name: "Releases" }));
    expect(await screen.findByText(/Connect GitHub to record releases/)).toBeInTheDocument();
  });

  it("shows the release view on the Releases tab once GitHub is connected", async () => {
    renderWithIntl(<ProjectDetail publicId="uuid-backend" />);

    await userEvent.click(screen.getByRole("tab", { name: "Releases" }));
    // Releases is stubbed, so the point is only that the connect prompt is not shown once connected.
    await waitFor(() =>
      expect(screen.queryByText(/Connect GitHub to record releases/)).not.toBeInTheDocument(),
    );
  });
});
