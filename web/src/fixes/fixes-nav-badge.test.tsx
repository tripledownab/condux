import { render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { ProjectStatus } from "@/src/issues/current-project";

const useUnviewedFixCountMock = vi.fn();
vi.mock("@/src/api/generated/condux", () => ({
  useUnviewedFixCount: () => useUnviewedFixCountMock(),
}));

const useCurrentProjectMock = vi.fn();
vi.mock("@/src/issues/current-project", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/src/issues/current-project")>()),
  useCurrentProject: () => useCurrentProjectMock(),
}));

import { FixesNavBadge } from "./fixes-nav-badge";

afterEach(() => vi.clearAllMocks());

describe("FixesNavBadge", () => {
  it("shows the count when there are unviewed fixes", () => {
    useCurrentProjectMock.mockReturnValue({
      status: ProjectStatus.Ready,
      project: { id: 7 },
      org: { id: 1 },
    });
    useUnviewedFixCountMock.mockReturnValue({ data: { data: { count: 3 } } });
    render(<FixesNavBadge collapsed={false} />);
    expect(screen.getByText("3")).toBeInTheDocument();
  });

  it("renders nothing when the count is zero", () => {
    useCurrentProjectMock.mockReturnValue({
      status: ProjectStatus.Ready,
      project: { id: 7 },
      org: { id: 1 },
    });
    useUnviewedFixCountMock.mockReturnValue({ data: { data: { count: 0 } } });
    const { container } = render(<FixesNavBadge collapsed={false} />);
    expect(container).toBeEmptyDOMElement();
  });

  it("renders nothing without a resolved project", () => {
    useCurrentProjectMock.mockReturnValue({ status: ProjectStatus.NoProjects, org: { id: 1 } });
    useUnviewedFixCountMock.mockReturnValue({ data: undefined });
    const { container } = render(<FixesNavBadge collapsed={false} />);
    expect(container).toBeEmptyDOMElement();
  });
});
