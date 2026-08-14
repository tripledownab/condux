import { screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { renderWithIntl } from "@/src/test-utils/render-with-intl";

const useAiFixUsageMock = vi.fn();
const useListReposMock = vi.fn();
const useListGithubBranchesMock = vi.fn();
vi.mock("@/src/api/generated/condux", () => ({
  useAiFixUsage: () => useAiFixUsageMock(),
  useListRepos: () => useListReposMock(),
  useListGithubBranches: () => useListGithubBranchesMock(),
}));

import { SuggestFixDialog } from "./suggest-fix-dialog";

function daysAgo(days: number): string {
  return new Date(Date.now() - days * 86_400_000).toISOString();
}

function render(lastSeen?: string) {
  renderWithIntl(
    <SuggestFixDialog
      open
      onOpenChange={vi.fn()}
      orgId={1}
      projectId={7}
      lastSeen={lastSeen}
      pending={false}
      onConfirm={vi.fn()}
    />,
  );
}

describe("SuggestFixDialog staleness warning", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    useAiFixUsageMock.mockReturnValue({
      data: { data: { remainingFixes: 5, uncappedFixes: false, capUsd: null, monthToDateUsd: 0 } },
    });
    useListReposMock.mockReturnValue({ data: { data: [] } });
    useListGithubBranchesMock.mockReturnValue({ data: undefined });
  });

  it("warns when the issue has gone quiet for long enough to have been fixed already", () => {
    render(daysAgo(30));

    expect(screen.getByText(/has not been seen for 30 days/i)).toBeTruthy();
  });

  it("says nothing about a still-firing issue", () => {
    render(daysAgo(1));

    expect(screen.queryByText(/has not been seen/i)).toBeNull();
  });

  it("says nothing when there is no issue at all", () => {
    // A CVE bump reuses this dialog and passes no issue, so a missing lastSeen must not read as
    // infinitely stale.
    render(undefined);

    expect(screen.queryByText(/has not been seen/i)).toBeNull();
  });

  it("warns but leaves the decision open, because only the reader knows if it still matters", () => {
    render(daysAgo(30));

    const confirm = screen.getAllByRole("button", { name: /suggest fix/i }).at(-1);
    expect((confirm as HTMLButtonElement).disabled).toBe(false);
  });
});
