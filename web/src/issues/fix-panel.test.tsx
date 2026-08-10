import { fireEvent, screen, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ConduxApiError } from "@/src/api/fetcher";
import { renderWithIntl } from "@/src/test-utils/render-with-intl";

vi.mock("@tanstack/react-query", () => ({
  useQueryClient: () => ({ invalidateQueries: vi.fn() }),
}));

const useListFixesMock = vi.fn();
const useRequestFixMock = vi.fn();
const useAiFixUsageMock = vi.fn();
const useListReposMock = vi.fn();
const useListGithubBranchesMock = vi.fn();
vi.mock("@/src/api/generated/condux", () => ({
  useListFixes: () => useListFixesMock(),
  useRequestFix: () => useRequestFixMock(),
  useAiFixUsage: () => useAiFixUsageMock(),
  useListRepos: () => useListReposMock(),
  useListGithubBranches: () => useListGithubBranchesMock(),
  getListFixesQueryKey: () => ["fixes"],
}));

import { FixPanel } from "./fix-panel";

const idle = { mutate: vi.fn(), isPending: false, isError: false, isSuccess: false, error: null };

// Open the confirmation dialog and click its confirm button (both the panel trigger and the dialog
// confirm read "Suggest fix", so the confirm is scoped to the dialog).
function confirmSuggest() {
  fireEvent.click(screen.getByRole("button", { name: "Suggest fix" }));
  const dialog = screen.getByRole("dialog");
  fireEvent.click(within(dialog).getByRole("button", { name: "Suggest fix" }));
}

beforeEach(() => {
  useAiFixUsageMock.mockReturnValue({
    data: { data: { monthToDateUsd: 0, capUsd: null, remainingFixes: 5, uncappedFixes: false } },
  });
  // No linked repo / no live branches by default; the repo-targeting tests override these.
  useListReposMock.mockReturnValue({ data: { data: [] } });
  useListGithubBranchesMock.mockReturnValue({ data: undefined });
});

afterEach(() => vi.clearAllMocks());

describe("FixPanel", () => {
  it("offers the trigger when the issue has no fix", () => {
    useListFixesMock.mockReturnValue({ data: { data: [] } });
    useRequestFixMock.mockReturnValue(idle);
    renderWithIntl(<FixPanel projectId={7} issueId="abc" orgId={1} />);
    expect(screen.getByRole("button", { name: "Suggest fix" })).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "View suggested fix" })).not.toBeInTheDocument();
  });

  it("links straight to the run once a fix exists, hiding the trigger", () => {
    useListFixesMock.mockReturnValue({ data: { data: [{ id: "fix-1" }] } });
    useRequestFixMock.mockReturnValue(idle);
    renderWithIntl(<FixPanel projectId={7} issueId="abc" orgId={1} />);
    expect(screen.getByRole("link", { name: "View suggested fix" })).toHaveAttribute(
      "href",
      "/fixes/fix-1",
    );
    expect(screen.queryByRole("button", { name: "Suggest fix" })).not.toBeInTheDocument();
  });

  it("points at the Fixes section right after a request, before the run row exists", () => {
    useListFixesMock.mockReturnValue({ data: { data: [] } });
    useRequestFixMock.mockReturnValue({ ...idle, isSuccess: true });
    renderWithIntl(<FixPanel projectId={7} issueId="abc" orgId={1} />);
    expect(screen.getByRole("link", { name: "View suggested fix" })).toHaveAttribute(
      "href",
      "/fixes",
    );
  });

  it("confirms before triggering a fix request, showing the remaining allowance", () => {
    const mutate = vi.fn();
    useListFixesMock.mockReturnValue({ data: { data: [] } });
    useRequestFixMock.mockReturnValue({ ...idle, mutate });
    renderWithIntl(<FixPanel projectId={7} issueId="abc" orgId={1} />);

    // Clicking the panel button opens the dialog and does NOT fire the request yet.
    fireEvent.click(screen.getByRole("button", { name: "Suggest fix" }));
    expect(screen.getByText("5 fixes remaining this period.")).toBeInTheDocument();
    expect(mutate).not.toHaveBeenCalled();

    // Confirming in the dialog fires it (no repo linked here, so the target defaults to null/null).
    fireEvent.click(
      within(screen.getByRole("dialog")).getByRole("button", { name: "Suggest fix" }),
    );
    expect(mutate).toHaveBeenCalledWith(
      { projectId: 7, issueId: "abc", data: { repoId: null, baseBranch: null } },
      expect.objectContaining({ onSuccess: expect.any(Function) }),
    );
  });

  it("targets the linked repo and its default branch by default", () => {
    const mutate = vi.fn();
    useListFixesMock.mockReturnValue({ data: { data: [] } });
    useRequestFixMock.mockReturnValue({ ...idle, mutate });
    useListReposMock.mockReturnValue({
      data: { data: [{ id: "repo-1", repoFullName: "acme/api", defaultBranch: "main" }] },
    });
    renderWithIntl(<FixPanel projectId={7} issueId="abc" orgId={1} />);

    fireEvent.click(screen.getByRole("button", { name: "Suggest fix" }));
    const dialog = screen.getByRole("dialog");
    // The dialog names the target repo + base branch before the run.
    expect(within(dialog).getByText("acme/api")).toBeInTheDocument();
    expect(within(dialog).getByText("main")).toBeInTheDocument();

    fireEvent.click(within(dialog).getByRole("button", { name: "Suggest fix" }));
    expect(mutate).toHaveBeenCalledWith(
      { projectId: 7, issueId: "abc", data: { repoId: "repo-1", baseBranch: "main" } },
      expect.objectContaining({ onSuccess: expect.any(Function) }),
    );
  });

  it("does not fire the request when the dialog is cancelled", () => {
    const mutate = vi.fn();
    useListFixesMock.mockReturnValue({ data: { data: [] } });
    useRequestFixMock.mockReturnValue({ ...idle, mutate });
    renderWithIntl(<FixPanel projectId={7} issueId="abc" orgId={1} />);
    fireEvent.click(screen.getByRole("button", { name: "Suggest fix" }));
    fireEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Cancel" }));
    expect(mutate).not.toHaveBeenCalled();
  });

  it("opens the connect-repo modal (not an inline error) when no repo is linked", () => {
    const mutate = vi.fn((_vars, opts) =>
      opts.onError?.(new ConduxApiError("POST", "/fix", 409, "no_repo_linked")),
    );
    useListFixesMock.mockReturnValue({ data: { data: [] } });
    useRequestFixMock.mockReturnValue({ ...idle, mutate });
    renderWithIntl(<FixPanel projectId={7} issueId="abc" orgId={1} />);
    confirmSuggest();
    expect(screen.getByText("Connect a repository")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Open Projects" })).toHaveAttribute(
      "href",
      "/projects",
    );
  });

  it("explains a reached compute ceiling instead of telling the user to try again", () => {
    useListFixesMock.mockReturnValue({ data: { data: [] } });
    useRequestFixMock.mockReturnValue({
      ...idle,
      isError: true,
      error: new ConduxApiError("POST", "/fix", 409, "ai_fix_cost_cap_exceeded"),
    });
    renderWithIntl(<FixPanel projectId={7} issueId="abc" orgId={1} />);
    // The cost cap is checked before the allowance, so an org with runs left can still be refused. This
    // used to be unmapped and fell through to "try again" — untrue, since retrying cannot succeed until
    // the month rolls over.
    expect(screen.queryByText(/try again/i)).not.toBeInTheDocument();
    expect(screen.getByText(/fair-use/i)).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "See plans" })).toHaveAttribute(
      "href",
      "/settings/general",
    );
  });

  it("offers a route to the plan once the monthly allowance is spent", () => {
    useListFixesMock.mockReturnValue({ data: { data: [] } });
    useRequestFixMock.mockReturnValue({
      ...idle,
      isError: true,
      error: new ConduxApiError("POST", "/fix", 409, "ai_fix_quota_exceeded"),
    });
    renderWithIntl(<FixPanel projectId={7} issueId="abc" orgId={1} />);
    // A spent allowance is recoverable by the user, so it is the one rejection that gets an action.
    expect(screen.getByRole("link", { name: "See plans" })).toHaveAttribute(
      "href",
      "/settings/general",
    );
  });

  it("refuses to send a request the allowance cannot cover", () => {
    useAiFixUsageMock.mockReturnValue({
      data: { data: { monthToDateUsd: 0, capUsd: 2, remainingFixes: 0, uncappedFixes: false } },
    });
    useListFixesMock.mockReturnValue({ data: { data: [] } });
    const mutate = vi.fn();
    useRequestFixMock.mockReturnValue({ ...idle, mutate });

    renderWithIntl(<FixPanel projectId={7} issueId="abc" orgId={1} />);
    fireEvent.click(screen.getByRole("button", { name: "Suggest fix" }));
    const dialog = screen.getByRole("dialog");

    // Confirming with nothing left is a guaranteed 409, so the dialog offers the plan instead of
    // letting the user spend a click on a rejection.
    expect(within(dialog).getByRole("button", { name: "Suggest fix" })).toBeDisabled();
    expect(within(dialog).getByRole("link", { name: "See plans" })).toHaveAttribute(
      "href",
      "/settings/general",
    );
    fireEvent.click(within(dialog).getByRole("button", { name: "Suggest fix" }));
    expect(mutate).not.toHaveBeenCalled();
  });

  it("refuses when the compute ceiling is reached even with runs left", () => {
    // remainingFixes is 2, so the count alone would wave this through. The server checks the cost cap
    // first, so the dialog has to as well or it invites a click that cannot succeed.
    useAiFixUsageMock.mockReturnValue({
      data: { data: { monthToDateUsd: 2, capUsd: 2, remainingFixes: 2, uncappedFixes: false } },
    });
    useListFixesMock.mockReturnValue({ data: { data: [] } });
    const mutate = vi.fn();
    useRequestFixMock.mockReturnValue({ ...idle, mutate });

    renderWithIntl(<FixPanel projectId={7} issueId="abc" orgId={1} />);
    fireEvent.click(screen.getByRole("button", { name: "Suggest fix" }));
    const dialog = screen.getByRole("dialog");

    expect(within(dialog).getByText(/fair-use/i)).toBeInTheDocument();
    expect(within(dialog).getByRole("button", { name: "Suggest fix" })).toBeDisabled();
    fireEvent.click(within(dialog).getByRole("button", { name: "Suggest fix" }));
    expect(mutate).not.toHaveBeenCalled();
  });

  it("still allows a request while the usage query is loading", () => {
    // Undefined usage must not read as exhausted, or a slow query would disable a button the org is
    // perfectly entitled to press.
    useAiFixUsageMock.mockReturnValue({ data: undefined });
    useListFixesMock.mockReturnValue({ data: { data: [] } });
    const mutate = vi.fn();
    useRequestFixMock.mockReturnValue({ ...idle, mutate });

    renderWithIntl(<FixPanel projectId={7} issueId="abc" orgId={1} />);
    confirmSuggest();
    expect(mutate).toHaveBeenCalled();
  });
});
