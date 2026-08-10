import { fireEvent, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { renderWithIntl } from "@/src/test-utils/render-with-intl";

// The guide resolves the current org, lists its projects, and reads the selected project's DSN keys.
// Mock those seams; keep the real OrgStatus values so the status switch matches.
const useCurrentOrgMock = vi.fn();
vi.mock("@/src/orgs/current-org", () => ({
  OrgStatus: { Loading: "loading", Error: "error", NoOrg: "no-org", Ready: "ready" },
  useCurrentOrg: () => useCurrentOrgMock(),
}));

vi.mock("@/src/projects/selected-project", () => ({
  useSelectedProject: () => ({ selectedProjectId: null }),
}));

const useListProjectsMock = vi.fn();
const useListKeysMock = vi.fn();
vi.mock("@/src/api/generated/condux", () => ({
  useListProjects: () => useListProjectsMock(),
  useListKeys: () => useListKeysMock(),
}));

import { DeveloperGuide } from "./developer-guide";

const PROJECT = {
  id: 7,
  orgId: 1,
  name: "Checkout",
  platform: "python",
  createdAt: "2026-01-01T00:00:00Z",
  publicId: "proj-uuid",
};

function ready() {
  useCurrentOrgMock.mockReturnValue({ status: "ready", org: { id: 1 }, role: "owner" });
  useListProjectsMock.mockReturnValue({
    isPending: false,
    isError: false,
    data: { data: [PROJECT] },
  });
  useListKeysMock.mockReturnValue({
    data: { data: [{ id: 10, publicKey: "pub123", label: "", isActive: true }] },
  });
}

describe("DeveloperGuide", () => {
  beforeEach(() => {
    useCurrentOrgMock.mockReset();
    useListProjectsMock.mockReset();
    useListKeysMock.mockReset();
  });

  it("shows the selected project's DSN and its platform snippets", () => {
    ready();
    renderWithIntl(<DeveloperGuide />);

    // The project's platform (python) is selected by default, so its install snippet shows.
    expect(screen.getByText("pip install condux")).toBeTruthy();
    // The DSN is built from the active key + project public id, and appears prefilled.
    expect(screen.getAllByText(/pub123@localhost:9010\/proj-uuid/).length).toBeGreaterThan(0);
  });

  it("switches snippets when another platform is chosen", async () => {
    ready();
    renderWithIntl(<DeveloperGuide />);

    fireEvent.click(screen.getByRole("combobox", { name: "Platform" }));
    fireEvent.click(await screen.findByRole("option", { name: "Go" }));

    expect(screen.getByText("go get github.com/tripledownab/condux/sdks/go")).toBeTruthy();
  });

  it("prompts to add a key when the project has no active key", () => {
    ready();
    useListKeysMock.mockReturnValue({ data: { data: [] } });
    renderWithIntl(<DeveloperGuide />);

    expect(screen.getByText(/no active key/i)).toBeTruthy();
  });

  it("prompts to create a project when the org has none", () => {
    useCurrentOrgMock.mockReturnValue({ status: "ready", org: { id: 1 }, role: "owner" });
    useListProjectsMock.mockReturnValue({ isPending: false, isError: false, data: { data: [] } });
    renderWithIntl(<DeveloperGuide />);

    expect(screen.getByText(/create a project first/i)).toBeTruthy();
  });

  it("shows a loading state while the org resolves", () => {
    useCurrentOrgMock.mockReturnValue({ status: "loading" });
    renderWithIntl(<DeveloperGuide />);

    expect(screen.getByText(/loading/i)).toBeTruthy();
  });
});
