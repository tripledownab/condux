import { fireEvent, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { renderWithIntl } from "@/src/test-utils/render-with-intl";

vi.mock("@tanstack/react-query", () => ({
  useQueryClient: () => ({ invalidateQueries: vi.fn() }),
}));

const useListReleasesMock = vi.fn();
const useListReposMock = vi.fn();
const recordMutate = vi.fn();
vi.mock("@/src/api/generated/condux", () => ({
  useListReleases: () => useListReleasesMock(),
  useListRepos: () => useListReposMock(),
  useRecordRelease: () => ({ mutate: recordMutate, isPending: false, isError: false }),
  getListReleasesQueryKey: () => ["releases"],
}));

import { Releases } from "./releases";

const repo = { id: "repo-1", repoFullName: "acme/api", defaultBranch: "main", projectId: 7 };

beforeEach(() => {
  useListReleasesMock.mockReturnValue({ data: { data: [] } });
  useListReposMock.mockReturnValue({ data: { data: [repo] } });
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

  it("prompts to link a repo when the project has none", () => {
    useListReposMock.mockReturnValue({ data: { data: [] } });
    renderWithIntl(<Releases projectId={7} canManage />);
    expect(screen.getByText(/Link a repository first/)).toBeInTheDocument();
  });

  it("is read-only for a non-admin (no record form)", () => {
    renderWithIntl(<Releases projectId={7} canManage={false} />);
    expect(screen.queryByRole("button", { name: "Record" })).not.toBeInTheDocument();
  });
});
