import { fireEvent, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { renderWithIntl } from "@/src/test-utils/render-with-intl";

const useGetIssueStatsMock = vi.fn();
vi.mock("@/src/api/generated/condux", () => ({
  useGetIssueStats: (projectId: number, issueId: string, params: { hours: number }) =>
    useGetIssueStatsMock(projectId, issueId, params),
}));

import { IssueChart } from "./issue-chart";

const buckets = [
  { ts: 1_789_196_400, count: 0 },
  { ts: 1_789_200_000, count: 3 },
  { ts: 1_789_203_600, count: 7 },
];

function statsResult(overrides: Record<string, unknown> = {}) {
  return {
    isPending: false,
    isError: false,
    isSuccess: true,
    data: { status: 200, data: { buckets, bucketSeconds: 3600 } },
    ...overrides,
  };
}

afterEach(() => vi.clearAllMocks());

describe("IssueChart", () => {
  it("renders a bar per bucket with the window total and axis labels", () => {
    useGetIssueStatsMock.mockReturnValue(statsResult());

    renderWithIntl(<IssueChart projectId={1} issueId="abc" />);

    expect(screen.getByText("10 events")).toBeInTheDocument();
    const chart = screen.getByRole("img", { name: "Event volume" });
    // One bar container per bucket, zero hours included; grid lines are spans and not counted.
    const bars = Array.from(chart.children).filter((el) => el.tagName === "DIV");
    expect(bars.length).toBe(3);
    expect(useGetIssueStatsMock).toHaveBeenCalledWith(1, "abc", { hours: 24 });

    // The count scale shows the window peak and the zero baseline.
    expect(screen.getByText("7")).toBeInTheDocument();
    expect(screen.getByText("0")).toBeInTheDocument();
    // Each bar carries a hover tooltip with its bucket time and count: an instant CSS one (the visible
    // span) plus the native title as a fallback.
    const peakBar = bars.at(-1) as HTMLElement;
    expect(peakBar.title).toMatch(/: 7 events$/);
    expect(peakBar.title).toMatch(/\w+ \d+/); // a formatted date, not a raw timestamp
    const tooltip = peakBar.querySelector("span");
    expect(tooltip?.textContent).toMatch(/: 7 events$/);
    expect(tooltip?.className).toContain("group-hover:block");
  });

  it("labels the time axis with the window start, middle and end", () => {
    useGetIssueStatsMock.mockReturnValue(statsResult());

    const { container } = renderWithIntl(<IssueChart projectId={1} issueId="abc" />);

    const axis = container.querySelector(".justify-between.pl-10");
    if (axis === null) {
      throw new Error("The time axis did not render.");
    }
    const labels = Array.from(axis.children).map((el) => el.textContent ?? "");
    expect(labels).toHaveLength(3);
    for (const label of labels) {
      expect(label).toMatch(/\w+ \d+/); // formatted dates, locale-aware
    }
  });

  it("switches the window when a range is selected", () => {
    useGetIssueStatsMock.mockReturnValue(statsResult());

    renderWithIntl(<IssueChart projectId={1} issueId="abc" />);
    fireEvent.click(screen.getByRole("button", { name: "7d" }));

    expect(useGetIssueStatsMock).toHaveBeenLastCalledWith(1, "abc", { hours: 168 });
  });

  it("shows loading and error states distinctly", () => {
    useGetIssueStatsMock.mockReturnValue(
      statsResult({ isPending: true, isSuccess: false, data: undefined }),
    );
    const { unmount } = renderWithIntl(<IssueChart projectId={1} issueId="abc" />);
    expect(screen.getByText("Loading volume.")).toBeInTheDocument();
    unmount();

    useGetIssueStatsMock.mockReturnValue(
      statsResult({ isError: true, isSuccess: false, data: undefined }),
    );
    renderWithIntl(<IssueChart projectId={1} issueId="abc" />);
    expect(screen.getByText("Could not load event volume.")).toBeInTheDocument();
  });
});
