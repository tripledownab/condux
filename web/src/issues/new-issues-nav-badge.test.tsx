import { render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

const useNewIssueCountMock = vi.fn();
vi.mock("@/src/api/generated/condux", () => ({
  useNewIssueCount: () => useNewIssueCountMock(),
}));

const useCurrentProjectMock = vi.fn();
// Keep the real module (the ProjectStatus enum) and override only the hook.
vi.mock("@/src/issues/current-project", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/src/issues/current-project")>()),
  useCurrentProject: () => useCurrentProjectMock(),
}));

import { ProjectStatus } from "@/src/issues/current-project";
import { NewIssuesNavBadge } from "./new-issues-nav-badge";

beforeEach(() => {
  useCurrentProjectMock.mockReturnValue({
    status: ProjectStatus.Ready,
    project: { id: 7 },
    org: { id: 1 },
  });
});

describe("NewIssuesNavBadge", () => {
  it("renders the count when there are new issues", () => {
    useNewIssueCountMock.mockReturnValue({ data: { data: { count: 4 } } });
    render(<NewIssuesNavBadge collapsed={false} />);
    expect(screen.getByText("4")).toBeInTheDocument();
  });

  it("renders nothing at zero", () => {
    useNewIssueCountMock.mockReturnValue({ data: { data: { count: 0 } } });
    const { container } = render(<NewIssuesNavBadge collapsed={false} />);
    expect(container).toBeEmptyDOMElement();
  });

  it("caps large counts at 99+", () => {
    useNewIssueCountMock.mockReturnValue({ data: { data: { count: 250 } } });
    render(<NewIssuesNavBadge collapsed={false} />);
    expect(screen.getByText("99+")).toBeInTheDocument();
  });

  it("shows a dot, not a number, when collapsed", () => {
    useNewIssueCountMock.mockReturnValue({ data: { data: { count: 4 } } });
    const { container } = render(<NewIssuesNavBadge collapsed />);
    expect(screen.queryByText("4")).not.toBeInTheDocument();
    expect(container.querySelector("span[aria-hidden]")).toBeInTheDocument();
  });
});
