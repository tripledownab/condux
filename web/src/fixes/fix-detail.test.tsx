import { fireEvent, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ProjectStatus } from "@/src/issues/current-project";
import { renderWithIntl } from "@/src/test-utils/render-with-intl";

const useGetFixMock = vi.fn();
const useMarkFixViewedMock = vi.fn();
const useArchiveFixMock = vi.fn();
vi.mock("@/src/api/generated/condux", () => ({
  useGetFix: () => useGetFixMock(),
  useMarkFixViewed: () => useMarkFixViewedMock(),
  useArchiveFix: () => useArchiveFixMock(),
  getListProjectFixesQueryKey: () => ["fixes"],
  getUnviewedFixCountQueryKey: () => ["count"],
}));

vi.mock("@tanstack/react-query", () => ({
  useQueryClient: () => ({ invalidateQueries: vi.fn() }),
}));

const useCurrentProjectMock = vi.fn();
vi.mock("@/src/issues/current-project", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/src/issues/current-project")>()),
  useCurrentProject: () => useCurrentProjectMock(),
}));

import { FixDetail } from "./fix-detail";

function succeededFix(overrides: Record<string, unknown> = {}) {
  return {
    id: "f1",
    issuePublicId: "0192f000-0000-7000-8000-000000000000",
    issueTitle: "TypeError: boom",
    issueLevel: 4,
    status: 3, // Succeeded
    verifyStatus: 0, // None
    repoFullName: "acme/api",
    provider: "fake",
    model: "claude-opus-4-8",
    branch: "condux/fix-1",
    prUrl: "https://example.invalid/acme/api/pull/1",
    summary: "Guards the `.amount` deref with a **money helper**.",
    createdAt: "2026-07-22T10:00:00Z",
    updatedAt: "2026-07-22T10:00:00Z",
    mergedAt: null,
    verifiedAt: null,
    archived: false,
    viewed: false,
    inputTokens: 1_200_000,
    outputTokens: 300_000,
    costUsd: 13.5, // 1.2M in @ $5/1M + 0.3M out @ $25/1M
    audit: [
      {
        actor: "conductor",
        event: "draft_pr_opened",
        detail: '{"inputTokens":1200,"outputTokens":300}',
        createdAt: "2026-07-22T10:01:00Z",
      },
    ],
    ...overrides,
  };
}

const markViewed = { mutate: vi.fn(), isPending: false };
const archive = { mutate: vi.fn(), isPending: false };

beforeEach(() => {
  useCurrentProjectMock.mockReturnValue({
    status: ProjectStatus.Ready,
    project: { id: 7 },
    org: { id: 1 },
  });
  useGetFixMock.mockReturnValue({
    data: { data: succeededFix() },
    isPending: false,
    isError: false,
    refetch: vi.fn(),
  });
  useMarkFixViewedMock.mockReturnValue(markViewed);
  useArchiveFixMock.mockReturnValue(archive);
});

afterEach(() => vi.clearAllMocks());

describe("FixDetail", () => {
  it("renders the summary as markdown (inline code and emphasis)", () => {
    renderWithIntl(<FixDetail fixId="f1" />);
    expect(screen.getByText(".amount")).toBeInTheDocument();
    expect(screen.getByText("money helper")).toBeInTheDocument();
  });

  it("shows the verification state, not the stale draft-PR status, once merged", () => {
    useGetFixMock.mockReturnValue({
      data: { data: succeededFix({ verifyStatus: 1, mergedAt: "2026-07-23T08:00:00Z" }) },
      isPending: false,
      isError: false,
      refetch: vi.fn(),
    });
    renderWithIntl(<FixDetail fixId="f1" />);
    expect(screen.getByText("Verifying in production")).toBeInTheDocument();
    expect(screen.queryByText("Draft PR ready")).not.toBeInTheDocument();
  });

  it("shows provider, model, compact token usage and the derived cost", () => {
    renderWithIntl(<FixDetail fixId="f1" />);
    expect(screen.getByText("claude-opus-4-8")).toBeInTheDocument();
    expect(screen.getByText("1.2M / 300K")).toBeInTheDocument();
    expect(screen.getByText("$13.50")).toBeInTheDocument();
  });

  it("omits the cost for a bring-your-own model we do not price", () => {
    useGetFixMock.mockReturnValue({
      data: { data: succeededFix({ model: "gpt-4o", costUsd: null }) },
      isPending: false,
      isError: false,
      refetch: vi.fn(),
    });
    renderWithIntl(<FixDetail fixId="f1" />);
    expect(screen.getByText("1.2M / 300K")).toBeInTheDocument();
    expect(screen.queryByText(/^\$/)).not.toBeInTheDocument();
  });

  it("puts View issue and View draft PR in the rail", () => {
    renderWithIntl(<FixDetail fixId="f1" />);
    expect(screen.getByRole("link", { name: "View issue" })).toHaveAttribute(
      "href",
      "/issues/0192f000-0000-7000-8000-000000000000",
    );
    expect(screen.getByRole("link", { name: "View draft PR" })).toHaveAttribute(
      "href",
      "https://example.invalid/acme/api/pull/1",
    );
  });

  it("marks the fix viewed on mount", () => {
    renderWithIntl(<FixDetail fixId="f1" />);
    expect(markViewed.mutate).toHaveBeenCalledWith(
      { projectId: 7, fixId: "f1" },
      expect.objectContaining({ onSuccess: expect.any(Function) }),
    );
  });

  it("archives the fix when the button is clicked", () => {
    renderWithIntl(<FixDetail fixId="f1" />);
    fireEvent.click(screen.getByRole("button", { name: "Archive" }));
    expect(archive.mutate).toHaveBeenCalledWith({
      projectId: 7,
      fixId: "f1",
      data: { archived: true },
    });
  });

  it("offers Restore for an archived fix", () => {
    useGetFixMock.mockReturnValue({
      data: { data: succeededFix({ archived: true }) },
      isPending: false,
      isError: false,
      refetch: vi.fn(),
    });
    renderWithIntl(<FixDetail fixId="f1" />);
    expect(screen.getByRole("button", { name: "Restore" })).toBeInTheDocument();
  });
});
