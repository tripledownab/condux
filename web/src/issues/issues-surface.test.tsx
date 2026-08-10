import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { renderWithIntl } from "@/src/test-utils/render-with-intl";

const useCurrentProjectMock = vi.fn();
// Keep the real module (notably the ProjectStatus enum) and override only the hook.
vi.mock("./current-project", async (importOriginal) => ({
  ...(await importOriginal<typeof import("./current-project")>()),
  useCurrentProject: () => useCurrentProjectMock(),
}));

// The list + counts hooks are argument-aware so the tests can assert what the surface asks the server
// for (filtering, ordering and paging are all server-side now — the surface only renders the page back).
const useListIssuesMock = vi.fn();
const useListIssueCountsMock = vi.fn();
const useListSavedViewsMock = vi.fn();
vi.mock("@/src/api/generated/condux", () => ({
  useListIssues: (...args: unknown[]) => useListIssuesMock(...args),
  useListIssueCounts: (...args: unknown[]) => useListIssueCountsMock(...args),
  useListSavedViews: () => useListSavedViewsMock(),
  useCreateSavedView: () => ({ mutate: vi.fn(), isPending: false }),
  useDeleteSavedView: () => ({ mutate: vi.fn(), isPending: false }),
  useMarkIssuesSeen: () => ({ mutate: vi.fn() }),
  getListSavedViewsQueryKey: () => ["views"],
  getNewIssueCountQueryKey: () => ["new-count"],
}));
vi.mock("@tanstack/react-query", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@tanstack/react-query")>()),
  useQueryClient: () => ({ invalidateQueries: vi.fn() }),
}));

vi.mock("next/navigation", () => ({ useParams: () => ({}), useRouter: () => ({ push: vi.fn() }) }));

import { ProjectStatus } from "./current-project";
import { IssuesSurface } from "./issues-surface";

const org = { id: 1, name: "Acme", slug: "acme", tier: 0, createdAt: "" };
const project = {
  id: 7,
  name: "web-app",
  orgId: 1,
  platform: "javascript",
  slug: "web-app",
  createdAt: "",
};

function issue(id: string, title: string, level = 4) {
  return {
    id,
    projectId: 7,
    fingerprint: `f-${id}`,
    title,
    culprit: `handle (src/${id}.ts:1)`,
    level,
    status: 1,
    firstSeen: new Date().toISOString(),
    lastSeen: new Date().toISOString(),
    eventCount: 3,
  };
}

const page = (issues: ReturnType<typeof issue>[], hasMore = false) => ({
  isPending: false,
  isError: false,
  isFetching: false,
  data: { data: { issues, hasMore } },
});
const counts = (byStatus: Record<string, number>, byLevel: Record<string, number>) => ({
  data: { data: { byStatus, byLevel } },
});
// The `q` the surface last asked the list endpoint for (the second arg is the params object).
const lastListQuery = () =>
  useListIssuesMock.mock.calls.at(-1)?.[1] as { q?: string; limit?: number };

beforeEach(() => {
  useListSavedViewsMock.mockReturnValue({ data: { data: [] } });
});

afterEach(() => vi.clearAllMocks());

describe("IssuesSurface", () => {
  it("shows a create-a-project prompt when the org has no projects", () => {
    useCurrentProjectMock.mockReturnValue({ status: ProjectStatus.NoProjects, org });
    useListIssuesMock.mockReturnValue({ isPending: false, isError: false, data: undefined });
    useListIssueCountsMock.mockReturnValue({ data: undefined });
    renderWithIntl(<IssuesSurface>main</IssuesSurface>);
    expect(screen.getByText(/No projects in Acme/i)).toBeInTheDocument();
  });

  it("renders the server page, the routed content and the rail counts", () => {
    useCurrentProjectMock.mockReturnValue({ status: ProjectStatus.Ready, org, project });
    useListIssuesMock.mockReturnValue(
      page([issue("a", "Boom in checkout"), issue("w", "Slow warn", 3)], true),
    );
    useListIssueCountsMock.mockReturnValue(counts({ "1": 5, "2": 2, "3": 1 }, { "4": 4, "3": 1 }));
    renderWithIntl(<IssuesSurface>main-pane-content</IssuesSurface>);

    // The surface renders exactly what the server returned — no client-side filtering.
    expect(screen.getByText("Boom in checkout")).toBeInTheDocument();
    expect(screen.getByText("Slow warn")).toBeInTheDocument();
    expect(screen.getByText("main-pane-content")).toBeInTheDocument();
    // Default view Unresolved; "2+" because the page reports more remaining.
    expect(screen.getByText("Unresolved · 2+ · by last seen")).toBeInTheDocument();

    // View counts come off the status facet; All is their sum. Severity counts off the level facet.
    expect(screen.getByRole("button", { name: "Unresolved5" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Resolved2" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "All8" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "error4" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "warning1" })).toBeInTheDocument();
  });

  it("asks the server for the selected view and severity", async () => {
    useCurrentProjectMock.mockReturnValue({ status: ProjectStatus.Ready, org, project });
    useListIssuesMock.mockReturnValue(page([issue("a", "Boom in checkout")]));
    useListIssueCountsMock.mockReturnValue(counts({ "1": 3, "2": 1 }, { "4": 3, "3": 1 }));
    renderWithIntl(<IssuesSurface>main</IssuesSurface>);

    // Selecting a built-in view swaps the query the list is fetched with.
    await userEvent.click(screen.getByRole("button", { name: "Resolved1" }));
    expect(lastListQuery().q).toBe("is:resolved");

    // A severity click composes onto the active query using the same token grammar the server parses.
    await userEvent.click(screen.getByRole("button", { name: "warning1" }));
    expect(lastListQuery().q).toBe("is:resolved level:warning");
    // Clicking it again clears the severity narrow.
    await userEvent.click(screen.getByRole("button", { name: "warning1" }));
    expect(lastListQuery().q).toBe("is:resolved");
  });

  it("grows the page size when Load more is clicked", async () => {
    useCurrentProjectMock.mockReturnValue({ status: ProjectStatus.Ready, org, project });
    useListIssuesMock.mockReturnValue(page([issue("a", "Boom in checkout")], true));
    useListIssueCountsMock.mockReturnValue(counts({ "1": 99 }, { "4": 99 }));
    renderWithIntl(<IssuesSurface>main</IssuesSurface>);

    expect(lastListQuery().limit).toBe(50);
    await userEvent.click(screen.getByRole("button", { name: "Load more" }));
    expect(lastListQuery().limit).toBe(100);
  });
});
