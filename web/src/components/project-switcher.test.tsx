import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { renderWithIntl } from "@/src/test-utils/render-with-intl";

// The current-project resolution, the projects query, and the selection store are faked; the Radix
// dropdown runs for real, so this exercises the switch interaction end to end.
const useCurrentProjectMock = vi.fn();
vi.mock("@/src/issues/current-project", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/src/issues/current-project")>()),
  useCurrentProject: () => useCurrentProjectMock(),
}));

const useListProjectsMock = vi.fn();
vi.mock("@/src/api/generated/condux", () => ({ useListProjects: () => useListProjectsMock() }));

const selectProject = vi.fn();
vi.mock("@/src/projects/selected-project", () => ({
  useSelectedProject: () => ({ selectedProjectId: 7, selectProject }),
}));

import { ProjectStatus } from "@/src/issues/current-project";
import { ProjectSwitcher } from "./project-switcher";

const org = { id: 1, name: "Acme", slug: "acme", tier: 0, createdAt: "" };
const web = {
  id: 7,
  name: "web-app",
  orgId: 1,
  platform: "javascript",
  slug: "web-app",
  createdAt: "",
};
const api = { id: 8, name: "api", orgId: 1, platform: "python", slug: "api", createdAt: "" };

afterEach(() => vi.clearAllMocks());

describe("ProjectSwitcher", () => {
  it("shows the current project and switches on select", async () => {
    useCurrentProjectMock.mockReturnValue({ status: ProjectStatus.Ready, org, project: web });
    useListProjectsMock.mockReturnValue({ data: { data: [web, api] } });
    renderWithIntl(<ProjectSwitcher />);

    const trigger = screen.getByRole("button", { name: /switch project/i });
    expect(trigger).toHaveTextContent("web-app");

    await userEvent.click(trigger);
    expect(await screen.findByRole("menuitemradio", { name: "web-app" })).toBeInTheDocument();
    await userEvent.click(screen.getByRole("menuitemradio", { name: "api" }));

    expect(selectProject).toHaveBeenCalledWith(8);
  });

  it("shows a loading label while the project resolves", () => {
    useCurrentProjectMock.mockReturnValue({ status: ProjectStatus.Loading });
    useListProjectsMock.mockReturnValue({ data: undefined });
    renderWithIntl(<ProjectSwitcher />);

    expect(screen.getByText("Loading project")).toBeInTheDocument();
  });
});
