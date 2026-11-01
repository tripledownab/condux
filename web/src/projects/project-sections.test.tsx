import { screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { ConduxApiError } from "@/src/api/fetcher";
import { renderWithIntl } from "@/src/test-utils/render-with-intl";

const linearMock = vi.fn();
vi.mock("@/src/api/generated/condux", () => ({ useGetLinearProject: () => linearMock() }));
// The panels are tested where they live; here only which tab exists and which one opens matters.
vi.mock("@/src/linear/linear-tab", () => ({ LinearTab: () => <p>linear panel</p> }));
vi.mock("@/src/settings/repositories", () => ({ Repositories: () => <p>github panel</p> }));
vi.mock("@/src/settings/dsn-keys", () => ({ DsnKeys: () => null }));
vi.mock("@/src/settings/mcp-tokens", () => ({ McpTokens: () => null }));
vi.mock("@/src/settings/release-tokens", () => ({ ReleaseTokens: () => null }));
vi.mock("@/src/settings/releases", () => ({ Releases: () => null }));
vi.mock("@/src/settings/use-github-connection", () => ({
  useGithubConnection: () => ({ loading: false, connected: true, notConfigured: false }),
}));

import { ProjectSections } from "./project-sections";

function renderSections() {
  return renderWithIntl(
    <ProjectSections projectId={1} publicId="abc" projectName="Backend" orgId={9} canManage />,
  );
}

afterEach(() => window.history.replaceState(null, "", "/"));

describe("ProjectSections", () => {
  // A deployment without Linear configured answers 404, and then there is no Linear tab at all.
  it("has no Linear tab when the server has no Linear", () => {
    linearMock.mockReturnValue({
      isPending: false,
      error: new ConduxApiError("GET", "/api/projects/1/linear", 404),
    });
    renderSections();
    expect(screen.queryByRole("tab", { name: "Linear" })).not.toBeInTheDocument();
  });

  // How a connect that left for Linear lands back where it started.
  it("opens on the tab the URL names", async () => {
    window.history.replaceState(null, "", "/projects/abc?tab=linear");
    linearMock.mockReturnValue({ isPending: false, error: null, data: { status: 200 } });
    renderSections();
    expect(await screen.findByText("linear panel")).toBeInTheDocument();
    expect(screen.queryByText("github panel")).not.toBeInTheDocument();
  });

  it("opens on GitHub otherwise", () => {
    linearMock.mockReturnValue({ isPending: false, error: null, data: { status: 200 } });
    renderSections();
    expect(screen.getByRole("tab", { name: "Linear" })).toBeInTheDocument();
    expect(screen.getByText("github panel")).toBeInTheDocument();
  });
});
