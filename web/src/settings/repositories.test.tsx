import { fireEvent, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { renderWithIntl } from "@/src/test-utils/render-with-intl";

vi.mock("@tanstack/react-query", () => ({
  useQueryClient: () => ({ invalidateQueries: vi.fn() }),
}));

const useListReposMock = vi.fn();
const useListCodeMappingsMock = vi.fn();
const useLinkRepoMock = vi.fn();
const useUnlinkRepoMock = vi.fn();
const useAddCodeMappingMock = vi.fn();
const useDeleteCodeMappingMock = vi.fn();
const useCveFindingsMock = vi.fn();
const useSuggestedMappingsMock = vi.fn();
const useListGithubRepositoriesMock = vi.fn();
const useListGithubBranchesMock = vi.fn();
vi.mock("@/src/api/generated/condux", () => ({
  useListGithubRepositories: () => useListGithubRepositoriesMock(),
  useListGithubBranches: () => useListGithubBranchesMock(),
  useListRepos: () => useListReposMock(),
  useListCodeMappings: () => useListCodeMappingsMock(),
  useLinkRepo: () => useLinkRepoMock(),
  useUnlinkRepo: () => useUnlinkRepoMock(),
  useAddCodeMapping: () => useAddCodeMappingMock(),
  useDeleteCodeMapping: () => useDeleteCodeMappingMock(),
  useCveFindings: () => useCveFindingsMock(),
  useSuggestedMappings: () => useSuggestedMappingsMock(),
  useListCveFixes: () => ({ data: { data: [] } }),
  useStartCveFix: () => ({ mutate: vi.fn(), isPending: false, isError: false }),
  getListReposQueryKey: () => ["repos"],
  getListCodeMappingsQueryKey: () => ["mappings"],
  getListCveFixesQueryKey: () => ["cve-fixes"],
}));

// The GitHub connection panel has its own suite; stub it so this stays focused on repo linking + mappings.
vi.mock("./github-connect", () => ({ GitHubConnect: () => null }));

// The add-repo form is gated on the org's GitHub connection state (a repo can't be linked before GitHub
// is connected); drive that state directly so the linking tests don't depend on the installations query.
const useGithubConnectionMock = vi.fn();
vi.mock("./use-github-connection", () => ({
  useGithubConnection: () => useGithubConnectionMock(),
}));

import { Repositories } from "./repositories";

const repo = {
  id: "repo-1",
  projectId: 7,
  repoFullName: "acme/api",
  defaultBranch: "main",
  createdAt: "2026-07-24T10:00:00Z",
};
const mapping = { id: "map-1", repoLinkId: "repo-1", stackRoot: "/app/dist/", sourceRoot: "src/" };

const idle = { mutate: vi.fn(), isPending: false };

beforeEach(() => {
  useListReposMock.mockReturnValue({ data: { data: [repo] }, isPending: false, isError: false });
  useListCodeMappingsMock.mockReturnValue({
    data: { data: [mapping] },
    isPending: false,
    isError: false,
  });
  useLinkRepoMock.mockReturnValue(idle);
  useUnlinkRepoMock.mockReturnValue(idle);
  useAddCodeMappingMock.mockReturnValue(idle);
  useDeleteCodeMappingMock.mockReturnValue(idle);
  useCveFindingsMock.mockReturnValue({ data: { data: [] } });
  useSuggestedMappingsMock.mockReturnValue({
    data: undefined,
    isFetched: false,
    isFetching: false,
    refetch: vi.fn(),
  });
  // acme/api is the repo already linked in this suite, so it exercises the already-linked filter.
  useListGithubRepositoriesMock.mockReturnValue({
    data: { status: 200, data: { repositories: ["acme/api", "acme/billing"] } },
  });
  useListGithubBranchesMock.mockReturnValue({
    data: { status: 200, data: { branches: ["main", "develop"] } },
  });
  useGithubConnectionMock.mockReturnValue({
    orgId: 1,
    loading: false,
    notConfigured: false,
    connected: true,
    canManage: true,
    installation: { installationId: 1, accountLogin: "acme", createdAt: "", manageUrl: "" },
    refetch: vi.fn(),
  });
});

afterEach(() => vi.clearAllMocks());

describe("Repositories", () => {
  it("lists linked repos and their code mappings", () => {
    renderWithIntl(<Repositories projectId={7} projectName="web-app" canManage />);
    expect(screen.getByText("acme/api")).toBeInTheDocument();
    expect(screen.getByText("/app/dist/ → src/")).toBeInTheDocument();
  });

  // Picked, not typed: a typed name can be one that does not exist or sits outside the installation,
  // which links fine and only fails later when a fix run cannot mint a token for it.
  it("links a repo picked from the installation (empty branch sends null)", async () => {
    const mutate = vi.fn();
    useLinkRepoMock.mockReturnValue({ ...idle, mutate });
    renderWithIntl(<Repositories projectId={7} projectName="web-app" canManage />);

    fireEvent.click(screen.getByRole("combobox", { name: "Repository" }));
    fireEvent.click(await screen.findByRole("option", { name: "billing" }));
    fireEvent.click(screen.getByRole("button", { name: "Link" }));
    expect(mutate).toHaveBeenCalledWith(
      { projectId: 7, data: { repoFullName: "acme/billing", defaultBranch: null } },
      expect.objectContaining({ onSuccess: expect.any(Function) }),
    );
  });

  it("sends the branch chosen for that repo", async () => {
    const mutate = vi.fn();
    useLinkRepoMock.mockReturnValue({ ...idle, mutate });
    renderWithIntl(<Repositories projectId={7} projectName="web-app" canManage />);

    fireEvent.click(screen.getByRole("combobox", { name: "Repository" }));
    fireEvent.click(await screen.findByRole("option", { name: "billing" }));
    fireEvent.click(screen.getByRole("combobox", { name: "Default branch" }));
    fireEvent.click(await screen.findByRole("option", { name: "develop" }));
    fireEvent.click(screen.getByRole("button", { name: "Link" }));

    expect(mutate).toHaveBeenCalledWith(
      { projectId: 7, data: { repoFullName: "acme/billing", defaultBranch: "develop" } },
      expect.objectContaining({ onSuccess: expect.any(Function) }),
    );
  });

  // Linking the same repo twice says nothing new, and an already-linked option is a dead end.
  it("leaves already-linked repos out of the picker", async () => {
    renderWithIntl(<Repositories projectId={7} projectName="web-app" canManage />);

    fireEvent.click(screen.getByRole("combobox", { name: "Repository" }));
    expect(await screen.findByRole("option", { name: "billing" })).toBeInTheDocument();
    expect(screen.queryByRole("option", { name: "api" })).not.toBeInTheDocument();
  });

  // GitHub returns them in its own order, which reads as unsorted once the list is more than a handful.
  it("lists the repos alphabetically, ignoring case", async () => {
    useListGithubRepositoriesMock.mockReturnValue({
      data: {
        status: 200,
        data: { repositories: ["acme/zebra", "acme/Alpha", "acme/mango", "acme/beta"] },
      },
    });
    renderWithIntl(<Repositories projectId={7} projectName="web-app" canManage />);

    fireEvent.click(screen.getByRole("combobox", { name: "Repository" }));
    await screen.findByRole("option", { name: "Alpha" });
    expect(screen.getAllByRole("option").map((option) => option.textContent)).toEqual([
      "Alpha",
      "beta",
      "mango",
      "zebra",
    ]);
  });

  // Dropping the owner is only safe because one installation means one account. If the list ever spans
  // owners, the bare names could collide, so the full name has to stay visible.
  it("keeps the owner shown when the repos do not all share one", async () => {
    useListGithubRepositoriesMock.mockReturnValue({
      data: { status: 200, data: { repositories: ["acme/billing", "other/billing"] } },
    });
    renderWithIntl(<Repositories projectId={7} projectName="web-app" canManage />);

    fireEvent.click(screen.getByRole("combobox", { name: "Repository" }));
    expect(await screen.findByRole("option", { name: "acme/billing" })).toBeInTheDocument();
    expect(screen.getByRole("option", { name: "other/billing" })).toBeInTheDocument();
  });

  // A self-host running without the GitHub App has no installation to ask, but still links repos for
  // source deep-links, so it keeps typing them.
  it("falls back to typing when the GitHub App is not configured", () => {
    const mutate = vi.fn();
    useLinkRepoMock.mockReturnValue({ ...idle, mutate });
    useGithubConnectionMock.mockReturnValue({
      orgId: 1,
      loading: false,
      notConfigured: true,
      connected: false,
      canManage: true,
      installation: undefined,
      refetch: vi.fn(),
    });
    renderWithIntl(<Repositories projectId={7} projectName="web-app" canManage />);

    expect((screen.getByLabelText("Repository") as HTMLElement).tagName).toBe("INPUT");
    fireEvent.change(screen.getByLabelText("Repository"), { target: { value: "acme/web" } });
    fireEvent.click(screen.getByRole("button", { name: "Link" }));
    expect(mutate).toHaveBeenCalledWith(
      { projectId: 7, data: { repoFullName: "acme/web", defaultBranch: null } },
      expect.objectContaining({ onSuccess: expect.any(Function) }),
    );
  });

  it("unlinks a repo after confirmation", () => {
    const mutate = vi.fn();
    useUnlinkRepoMock.mockReturnValue({ ...idle, mutate });
    renderWithIntl(<Repositories projectId={7} projectName="web-app" canManage />);

    fireEvent.click(screen.getByRole("button", { name: "Unlink" }));
    fireEvent.click(screen.getByRole("button", { name: "Confirm" }));
    expect(mutate).toHaveBeenCalledWith(
      { projectId: 7, repoId: "repo-1" },
      expect.objectContaining({ onSuccess: expect.any(Function) }),
    );
  });

  it("adds and deletes a code mapping", () => {
    const add = vi.fn();
    const del = vi.fn();
    useAddCodeMappingMock.mockReturnValue({ ...idle, mutate: add });
    useDeleteCodeMappingMock.mockReturnValue({ ...idle, mutate: del });
    renderWithIntl(<Repositories projectId={7} projectName="web-app" canManage />);

    fireEvent.change(screen.getByLabelText("Stack path prefix"), { target: { value: "/build/" } });
    fireEvent.click(screen.getByRole("button", { name: "Add mapping" }));
    expect(add).toHaveBeenCalledWith(
      { projectId: 7, repoId: "repo-1", data: { stackRoot: "/build/", sourceRoot: null } },
      expect.objectContaining({ onSuccess: expect.any(Function) }),
    );

    fireEvent.click(screen.getByRole("button", { name: "Delete" }));
    expect(del).toHaveBeenCalledWith(
      { projectId: 7, repoId: "repo-1", mappingId: "map-1" },
      expect.objectContaining({ onSuccess: expect.any(Function) }),
    );
  });

  it("fetches suggestions on click and adds one", () => {
    const refetch = vi.fn();
    const add = vi.fn();
    useAddCodeMappingMock.mockReturnValue({ ...idle, mutate: add });
    useSuggestedMappingsMock.mockReturnValue({
      data: { data: [{ stackRoot: "/build/", sourceRoot: "web/", matchCount: 4 }] },
      isFetched: true,
      isFetching: false,
      refetch,
    });
    renderWithIntl(<Repositories projectId={7} projectName="web-app" canManage />);

    fireEvent.click(screen.getByRole("button", { name: "Suggest from errors" }));
    expect(refetch).toHaveBeenCalled();

    // The suggestion renders with its support count; adding it calls addCodeMapping with the derived rule.
    expect(screen.getByText("/build/ → web/")).toBeInTheDocument();
    expect(screen.getByText("4 frames")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Add" }));
    expect(add).toHaveBeenCalledWith(
      { projectId: 7, repoId: "repo-1", data: { stackRoot: "/build/", sourceRoot: "web/" } },
      expect.objectContaining({ onSuccess: expect.any(Function) }),
    );
  });

  it("hides the add-repo form until GitHub is connected", () => {
    useGithubConnectionMock.mockReturnValue({
      orgId: 1,
      loading: false,
      notConfigured: false,
      connected: false,
      canManage: true,
      account: null,
    });
    renderWithIntl(<Repositories projectId={7} projectName="web-app" canManage />);

    // Linked repos still list, but linking a new one is gated behind connecting GitHub first.
    expect(screen.getByText("acme/api")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Link" })).not.toBeInTheDocument();
    expect(screen.queryByLabelText("Repository")).not.toBeInTheDocument();
  });

  it("allows linking when the GitHub App is not configured on the server (self-host)", () => {
    useGithubConnectionMock.mockReturnValue({
      orgId: 0,
      loading: false,
      notConfigured: true,
      connected: false,
      canManage: false,
      account: null,
    });
    renderWithIntl(<Repositories projectId={7} projectName="web-app" canManage />);

    expect(screen.getByRole("button", { name: "Link" })).toBeInTheDocument();
  });

  it("is read-only for a non-admin (no link, unlink, or mapping controls)", () => {
    renderWithIntl(<Repositories projectId={7} projectName="web-app" canManage={false} />);
    expect(screen.getByText("acme/api")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Link" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Unlink" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Add mapping" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Delete" })).not.toBeInTheDocument();
  });
});
