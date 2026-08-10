import { screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { OrgStatus } from "@/src/orgs/current-org";
import { renderWithIntl } from "@/src/test-utils/render-with-intl";

vi.mock("@tanstack/react-query", () => ({
  useQueryClient: () => ({ invalidateQueries: vi.fn() }),
}));

const useListProjectsMock = vi.fn();
const useCreateProjectMock = vi.fn();
vi.mock("@/src/api/generated/condux", () => ({
  useListProjects: () => useListProjectsMock(),
  useCreateProject: () => useCreateProjectMock(),
  getListProjectsQueryKey: () => ["projects"],
}));

const useCurrentOrgMock = vi.fn();
vi.mock("@/src/orgs/current-org", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/src/orgs/current-org")>()),
  useCurrentOrg: () => useCurrentOrgMock(),
}));

import { ProjectsIndex } from "./projects-index";

const twoProjects = {
  data: {
    data: [
      {
        id: 7,
        publicId: "uuid-backend",
        orgId: 1,
        name: "Backend",
        platform: "python",
        createdAt: "2026-01-01",
      },
      {
        id: 8,
        publicId: "uuid-web",
        orgId: 1,
        name: "Web",
        platform: "javascript",
        createdAt: "2026-01-01",
      },
    ],
  },
  isPending: false,
  isError: false,
};

function asRole(role: string) {
  useCurrentOrgMock.mockReturnValue({ status: OrgStatus.Ready, org: { id: 1 }, role });
}

beforeEach(() => {
  asRole("admin");
  useListProjectsMock.mockReturnValue(twoProjects);
  useCreateProjectMock.mockReturnValue({ mutate: vi.fn(), isPending: false, isError: false });
});

afterEach(() => vi.clearAllMocks());

describe("ProjectsIndex", () => {
  it("lists each project as a link to its detail page", () => {
    renderWithIntl(<ProjectsIndex />);
    const backend = screen.getByRole("link", { name: /Backend/ });
    expect(backend).toHaveAttribute("href", "/projects/uuid-backend");
    expect(screen.getByRole("link", { name: /Web/ })).toHaveAttribute("href", "/projects/uuid-web");
  });

  it("shows the empty state when the org has no projects", () => {
    useListProjectsMock.mockReturnValue({ data: { data: [] }, isPending: false, isError: false });
    renderWithIntl(<ProjectsIndex />);
    expect(screen.getByText(/No projects yet/)).toBeInTheDocument();
  });

  it("shows the create form for an admin", () => {
    renderWithIntl(<ProjectsIndex />);
    expect(screen.getByRole("button", { name: /Create/i })).toBeInTheDocument();
  });

  it("hides the create form from a member", () => {
    asRole("member");
    renderWithIntl(<ProjectsIndex />);
    expect(screen.queryByRole("button", { name: /Create/i })).not.toBeInTheDocument();
  });
});
