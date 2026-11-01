import { fireEvent, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ConduxApiError } from "@/src/api/fetcher";
import { renderWithIntl } from "@/src/test-utils/render-with-intl";

vi.mock("@tanstack/react-query", () => ({
  useQueryClient: () => ({ invalidateQueries: vi.fn() }),
}));

const useListReleasesMock = vi.fn();
const useListReposMock = vi.fn();
const recordMutate = vi.fn();
const useRecordReleaseMock = vi.fn();
vi.mock("@/src/api/generated/condux", () => ({
  useListReleases: () => useListReleasesMock(),
  useListRepos: () => useListReposMock(),
  useRecordRelease: () => useRecordReleaseMock(),
  getListReleasesQueryKey: () => ["releases"],
}));

import { Releases } from "./releases";

const repo = { id: "repo-1", repoFullName: "acme/api", defaultBranch: "main", projectId: 7 };

beforeEach(() => {
  useListReleasesMock.mockReturnValue({ data: { data: [] } });
  useListReposMock.mockReturnValue({ data: { data: [repo] } });
  useRecordReleaseMock.mockReturnValue({ mutate: recordMutate, isPending: false, isError: false });
});
afterEach(() => vi.clearAllMocks());

describe("Releases", () => {
  it("lists recorded releases with the repo name and short commit", () => {
    useListReleasesMock.mockReturnValue({
      data: {
        data: [
          {
            id: "r1",
            projectId: 7,
            repoLinkId: "repo-1",
            version: "v1.2.3",
            commitSha: "abcdef1234567890",
            createdAt: "2026-07-20T00:00:00Z",
          },
        ],
      },
    });
    // canManage=false so the repo picker (which would also render "acme/api") is absent.
    renderWithIntl(<Releases projectId={7} canManage={false} />);

    expect(screen.getByText("v1.2.3")).toBeInTheDocument();
    expect(screen.getByText("abcdef12")).toBeInTheDocument();
    expect(screen.getByText("acme/api")).toBeInTheDocument();
  });

  it("records a release, defaulting to the sole linked repo", () => {
    renderWithIntl(<Releases projectId={7} canManage />);

    fireEvent.change(screen.getByLabelText("Version"), { target: { value: "v2.0.0" } });
    fireEvent.change(screen.getByLabelText("Commit SHA"), { target: { value: "deadbeef" } });
    fireEvent.click(screen.getByRole("button", { name: "Record" }));

    expect(recordMutate).toHaveBeenCalledWith(
      { projectId: 7, data: { repoLinkId: "repo-1", version: "v2.0.0", commitSha: "deadbeef" } },
      expect.objectContaining({ onSuccess: expect.any(Function) }),
    );
  });

  // Each hint names only what recording needs, and says the release tokens below do not need it.
  it("says recording needs a linked repo when the project has none", () => {
    useListReposMock.mockReturnValue({ data: { data: [] } });
    renderWithIntl(<Releases projectId={7} canManage />);
    expect(
      screen.getByText(/Recording a release here needs a linked repository/),
    ).toBeInTheDocument();
    expect(screen.getByText(/can still upload source maps and debug files/)).toBeInTheDocument();
  });

  it("says to connect GitHub first when it is not connected and no repo is linked", () => {
    useListReposMock.mockReturnValue({ data: { data: [] } });
    renderWithIntl(<Releases projectId={7} canManage needsGithub />);
    expect(screen.getByText(/which needs GitHub connected first/)).toBeInTheDocument();
    expect(screen.queryByText(/needs a linked repository\./)).not.toBeInTheDocument();
  });

  // The server records against any linked repo, GitHub connected or not.
  it("still offers the record form while GitHub is disconnected if a repo is linked", () => {
    renderWithIntl(<Releases projectId={7} canManage needsGithub />);
    expect(screen.getByRole("button", { name: "Record" })).toBeInTheDocument();
  });

  it("is read-only for a non-admin (no record form)", () => {
    renderWithIntl(<Releases projectId={7} canManage={false} />);
    expect(screen.queryByRole("button", { name: "Record" })).not.toBeInTheDocument();
  });

  // A refusal the person can fix says what to fix; "Try again" would send the same commit again.
  it.each([
    ["invalid_commit", "Enter the commit as its SHA: 7 to 64 hex characters."],
    ["invalid_version", "Enter a version."],
    ["something_else", "Could not record the release. Try again."],
    ["constructor", "Could not record the release. Try again."],
  ])("explains the server's %s refusal", (code, message) => {
    useRecordReleaseMock.mockReturnValue({
      mutate: recordMutate,
      isPending: false,
      isError: true,
      error: new ConduxApiError("POST", "/api/projects/7/releases", 400, code),
    });
    renderWithIntl(<Releases projectId={7} canManage />);
    expect(screen.getByText(message)).toBeInTheDocument();
  });
});
