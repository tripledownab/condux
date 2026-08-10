import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { renderWithIntl } from "@/src/test-utils/render-with-intl";

vi.mock("@tanstack/react-query", () => ({
  useQueryClient: () => ({ invalidateQueries: vi.fn() }),
}));

const push = vi.fn();
vi.mock("next/navigation", () => ({ useRouter: () => ({ push }) }));

const completeMutate = vi.fn();

const useCurrentProjectMock = vi.fn();
vi.mock("@/src/issues/current-project", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/src/issues/current-project")>()),
  useCurrentProject: () => useCurrentProjectMock(),
}));

const useListReposMock = vi.fn();
const useListIssuesMock = vi.fn();
const useListKeysMock = vi.fn();
vi.mock("@/src/api/generated/condux", () => ({
  useCreateProject: () => ({ mutate: vi.fn(), isPending: false, isError: false }),
  getListProjectsQueryKey: () => ["projects"],
  useLinkRepo: () => ({ mutate: vi.fn(), isPending: false }),
  getListReposQueryKey: () => ["repos"],
  useListRepos: () => useListReposMock(),
  useListIssues: () => useListIssuesMock(),
  useListKeys: () => useListKeysMock(),
  useCreateOrg: () => ({ mutate: vi.fn(), isPending: false, isError: false }),
  getListMyOrgsQueryKey: () => ["orgs"],
  useCreateInvite: () => ({ mutate: vi.fn(), isPending: false, isError: false }),
  useCompleteOnboarding: () => ({ mutate: completeMutate, isPending: false }),
  getMeQueryKey: () => ["me"],
}));

// The per-project GitHub connection panel (RepoStep renders it) has its own suite; stub it here.
vi.mock("@/src/settings/github-connect", () => ({ GitHubConnect: () => null }));

import { ProjectStatus } from "@/src/issues/current-project";
import { Onboarding } from "./onboarding";

const org = { id: 1, name: "Acme", slug: "acme", tier: 0, createdAt: "" };
const project = {
  id: 7,
  publicId: "proj-uuid-7",
  name: "web-app",
  orgId: 1,
  platform: "javascript",
  createdAt: "",
};

afterEach(() => vi.clearAllMocks());

describe("Onboarding", () => {
  it("prompts to create the first project and locks later steps when the org has none", () => {
    useCurrentProjectMock.mockReturnValue({ status: ProjectStatus.NoProjects, org });
    renderWithIntl(<Onboarding />);

    expect(screen.getByRole("heading", { name: "Get started" })).toBeInTheDocument();
    expect(screen.getByText("Create your first project")).toBeInTheDocument();
    expect(screen.getByLabelText("Project name")).toBeInTheDocument();
    // Steps 3 and 4 are locked until a project exists.
    expect(screen.getAllByText("Finish the previous step first.")).toHaveLength(2);
  });

  it("shows the connect-repo form, the DSN and a waiting hint before the first event", () => {
    useCurrentProjectMock.mockReturnValue({ status: ProjectStatus.Ready, org, project });
    useListReposMock.mockReturnValue({ data: { data: [] } });
    useListIssuesMock.mockReturnValue({ data: { data: { issues: [], hasMore: false } } });
    useListKeysMock.mockReturnValue({
      data: { data: [{ id: 1, publicKey: "abc", isActive: true, projectId: 7 }] },
    });
    renderWithIntl(<Onboarding />);

    expect(screen.getByPlaceholderText("owner/repository")).toBeInTheDocument();
    expect(screen.getByText("http://abc@localhost:9010/proj-uuid-7")).toBeInTheDocument();
    expect(screen.getByText("Waiting for your first event.")).toBeInTheDocument();
  });

  it("marks the repo and event steps done once a repo is linked and an event has arrived", () => {
    useCurrentProjectMock.mockReturnValue({ status: ProjectStatus.Ready, org, project });
    useListReposMock.mockReturnValue({ data: { data: [{ repoFullName: "acme/api" }] } });
    useListIssuesMock.mockReturnValue({
      data: { data: { issues: [{ id: "i1" }], hasMore: false } },
    });
    useListKeysMock.mockReturnValue({ data: { data: [] } });
    renderWithIntl(<Onboarding />);

    expect(screen.getByText("Linked to acme/api.")).toBeInTheDocument();
    expect(screen.getByText("Your first event arrived. You are all set.")).toBeInTheDocument();
  });

  it("disables Finish until a project exists", () => {
    useCurrentProjectMock.mockReturnValue({ status: ProjectStatus.NoProjects, org });
    renderWithIntl(<Onboarding />);

    expect(screen.getByRole("button", { name: "Finish setup" })).toBeDisabled();
    expect(screen.getByText(/Create a project to finish/)).toBeInTheDocument();
  });

  it("finishes onboarding and goes to the dashboard once a project exists", async () => {
    completeMutate.mockImplementation((_vars, opts) => opts?.onSuccess?.());
    useCurrentProjectMock.mockReturnValue({ status: ProjectStatus.Ready, org, project });
    useListReposMock.mockReturnValue({ data: { data: [] } });
    useListIssuesMock.mockReturnValue({ data: { data: { issues: [], hasMore: false } } });
    useListKeysMock.mockReturnValue({ data: { data: [] } });
    renderWithIntl(<Onboarding />);

    const finish = screen.getByRole("button", { name: "Finish setup" });
    expect(finish).toBeEnabled();
    await userEvent.click(finish);
    expect(completeMutate).toHaveBeenCalled();
    await waitFor(() => expect(push).toHaveBeenCalledWith("/"));
  });
});
