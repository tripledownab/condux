import { fireEvent, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ConduxApiError } from "@/src/api/fetcher";
import { ProjectStatus } from "@/src/issues/current-project";
import { renderWithIntl } from "@/src/test-utils/render-with-intl";

const useGetFixMock = vi.fn();
const useMarkFixViewedMock = vi.fn();
const useArchiveFixMock = vi.fn();
const useCancelFixMock = vi.fn();
const useCurrentOrgMock = vi.fn();
vi.mock("@/src/orgs/current-org", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/src/orgs/current-org")>()),
  useCurrentOrg: () => useCurrentOrgMock(),
}));
vi.mock("@/src/api/generated/condux", () => ({
  useGetFix: () => useGetFixMock(),
  useMarkFixViewed: () => useMarkFixViewedMock(),
  useArchiveFix: () => useArchiveFixMock(),
  useCancelFix: () => useCancelFixMock(),
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
        createdAt: "2026-07-22T10:01:00Z",
      },
    ],
    ...overrides,
  };
}

const markViewed = { mutate: vi.fn(), isPending: false };
const archive = { mutate: vi.fn(), isPending: false };
const cancel = { mutate: vi.fn(), isPending: false, error: null };

beforeEach(() => {
  useCurrentProjectMock.mockReturnValue({
    status: ProjectStatus.Ready,
    project: { id: 7, publicId: "0192f000-0000-7000-8000-00000000abcd" },
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
  useCancelFixMock.mockReturnValue(cancel);
  useCurrentOrgMock.mockReturnValue({ status: "ready", org: { id: 1 }, role: "admin" });
});

afterEach(() => vi.clearAllMocks());

function failedFix(failureReason: number | null) {
  return succeededFix({ status: 4, prUrl: "", branch: "", summary: "", failureReason });
}

describe("FixDetail failure reason", () => {
  it("says a missing code mapping is why, and links to the project to add one", () => {
    useGetFixMock.mockReturnValue({
      data: { data: failedFix(1) },
      isPending: false,
      refetch: vi.fn(),
    });
    renderWithIntl(<FixDetail fixId="f1" />);

    expect(screen.getByText(/No frame in the stack trace pointed to a file/)).toBeInTheDocument();
    expect(
      screen.getByRole("link", { name: "Open the project's GitHub settings" }),
    ).toHaveAttribute("href", "/projects/0192f000-0000-7000-8000-00000000abcd");
  });

  it("sends a rejected model key to the AI provider settings", () => {
    useGetFixMock.mockReturnValue({
      data: { data: failedFix(3) },
      isPending: false,
      refetch: vi.fn(),
    });
    renderWithIntl(<FixDetail fixId="f1" />);

    expect(screen.getByRole("link", { name: "Open the AI provider settings" })).toHaveAttribute(
      "href",
      "/settings/provider",
    );
  });

  it("says to retry from the issue when trying again is the remedy", () => {
    useGetFixMock.mockReturnValue({
      data: { data: failedFix(6) },
      isPending: false,
      refetch: vi.fn(),
    });
    renderWithIntl(<FixDetail fixId="f1" />);

    expect(screen.getByText(/took too long/)).toBeInTheDocument();
    expect(screen.getByText(/Open the issue to request the fix again/)).toBeInTheDocument();
  });

  it("offers no next step when trying again cannot help", () => {
    useGetFixMock.mockReturnValue({
      data: { data: failedFix(11) },
      isPending: false,
      refetch: vi.fn(),
    });
    renderWithIntl(<FixDetail fixId="f1" />);

    expect(screen.getByText(/none could be read as text/)).toBeInTheDocument();
    expect(screen.queryByText(/Open the issue to request the fix again/)).not.toBeInTheDocument();
  });

  it("reads a reason this build does not know as unclassified rather than guessing", () => {
    useGetFixMock.mockReturnValue({
      data: { data: failedFix(99) },
      isPending: false,
      refetch: vi.fn(),
    });
    renderWithIntl(<FixDetail fixId="f1" />);

    expect(screen.getByText(/could not classify/)).toBeInTheDocument();
  });

  it("claims nothing for a run that did not fail, or failed before reasons were recorded", () => {
    renderWithIntl(<FixDetail fixId="f1" />);
    expect(screen.queryByText("Why it failed")).not.toBeInTheDocument();

    useGetFixMock.mockReturnValue({
      data: { data: failedFix(null) },
      isPending: false,
      refetch: vi.fn(),
    });
    renderWithIntl(<FixDetail fixId="f1" />);
    expect(screen.queryByText("Why it failed")).not.toBeInTheDocument();
  });
});

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

  it("leaves out a provider and model that are not known yet", () => {
    // A hosted run waiting in the queue has no provider until the Conductor takes it.
    useGetFixMock.mockReturnValue({
      data: {
        data: succeededFix({ status: 1, provider: "", model: "", inputTokens: 0, outputTokens: 0 }),
      },
      isPending: false,
      isError: false,
      refetch: vi.fn(),
    });
    renderWithIntl(<FixDetail fixId="f1" />);
    expect(screen.queryByText(/Provider:/)).not.toBeInTheDocument();
    expect(screen.queryByText(/Model:/)).not.toBeInTheDocument();
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

describe("FixDetail cancel", () => {
  const pendingFix = () => succeededFix({ status: 1, prUrl: "", branch: "", summary: "" });

  it("lets an admin cancel a run nothing has taken yet", () => {
    useGetFixMock.mockReturnValue({
      data: { data: pendingFix() },
      isPending: false,
      refetch: vi.fn(),
    });
    renderWithIntl(<FixDetail fixId="f1" />);

    fireEvent.click(screen.getByRole("button", { name: "Cancel run" }));

    expect(cancel.mutate).toHaveBeenCalledWith({ projectId: 7, fixId: "f1" });
  });

  it("offers no cancel to a member", () => {
    useCurrentOrgMock.mockReturnValue({ status: "ready", org: { id: 1 }, role: "member" });
    useGetFixMock.mockReturnValue({
      data: { data: pendingFix() },
      isPending: false,
      refetch: vi.fn(),
    });
    renderWithIntl(<FixDetail fixId="f1" />);

    expect(screen.queryByRole("button", { name: "Cancel run" })).not.toBeInTheDocument();
  });

  it("offers the cancel for a runner's run that has started, whose lease may have lapsed", () => {
    useGetFixMock.mockReturnValue({
      data: { data: succeededFix({ status: 2, provider: "runner" }) },
      isPending: false,
      refetch: vi.fn(),
    });
    renderWithIntl(<FixDetail fixId="f1" />);

    expect(screen.getByRole("button", { name: "Cancel run" })).toBeInTheDocument();
  });

  it("offers no cancel once the Conductor has started the run", () => {
    useGetFixMock.mockReturnValue({
      data: { data: succeededFix({ status: 2 }) },
      isPending: false,
      refetch: vi.fn(),
    });
    renderWithIntl(<FixDetail fixId="f1" />);

    expect(screen.queryByRole("button", { name: "Cancel run" })).not.toBeInTheDocument();
  });

  it("says the run can no longer be cancelled when the server refuses", () => {
    useCancelFixMock.mockReturnValue({
      ...cancel,
      error: new ConduxApiError(
        "POST",
        "/api/projects/7/fixes/f1/cancel",
        409,
        "fix_not_cancellable",
      ),
    });
    useGetFixMock.mockReturnValue({
      data: { data: pendingFix() },
      isPending: false,
      refetch: vi.fn(),
    });
    renderWithIntl(<FixDetail fixId="f1" />);

    expect(screen.getByText(/can no longer be cancelled/)).toBeInTheDocument();
  });

  it("says only that the cancel failed for any other error", () => {
    // A network failure or a 503 says nothing about the run, so the reader is not told it ended.
    useCancelFixMock.mockReturnValue({
      ...cancel,
      error: new ConduxApiError("POST", "/api/projects/7/fixes/f1/cancel", 503),
    });
    useGetFixMock.mockReturnValue({
      data: { data: pendingFix() },
      isPending: false,
      refetch: vi.fn(),
    });
    renderWithIntl(<FixDetail fixId="f1" />);

    expect(screen.getByText("Could not cancel the run")).toBeInTheDocument();
    expect(screen.queryByText(/can no longer be cancelled/)).not.toBeInTheDocument();
  });
});
