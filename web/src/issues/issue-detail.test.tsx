import { screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { renderWithIntl } from "@/src/test-utils/render-with-intl";

const useCurrentProjectMock = vi.fn();
vi.mock("./current-project", async (importOriginal) => ({
  ...(await importOriginal<typeof import("./current-project")>()),
  useCurrentProject: () => useCurrentProjectMock(),
}));

const useGetIssueMock = vi.fn();
vi.mock("@/src/api/generated/condux", () => ({
  useGetIssue: () => useGetIssueMock(),
  useUpdateIssueStatus: () => ({ mutate: vi.fn(), isPending: false }),
  useListMembers: () => ({ data: undefined }),
  useMe: () => ({ data: undefined }),
  getGetIssueQueryKey: () => ["issue"],
  getListIssuesQueryKey: () => ["issues"],
}));

// The Conductor panel has its own test (fix-panel.test.tsx) and its own data hooks; stub it here so
// this test stays focused on the issue header + events.
vi.mock("./fix-panel", () => ({ FixPanel: () => null }));
// The actions rail has its own suite (issue-actions.test.tsx) and needs a QueryClient; stub it here.
vi.mock("./issue-actions", () => ({ IssueActions: () => null }));
// The chart likewise has its own test (issue-chart.test.tsx) and its own stats hook.
vi.mock("./issue-chart", () => ({ IssueChart: () => null }));
// The notes panel has its own suite (issue-notes.test.tsx) and needs a QueryClient; stub it here.
vi.mock("./issue-notes", () => ({ IssueNotes: () => null }));
// The Linear action has its own tests and reads the Linear routes.
vi.mock("./issue-ticket", () => ({ IssueTicket: () => null }));
// The events table lazy-loads via its own hook; it has its own suite (events-table.test.tsx).
vi.mock("./events-table", () => ({ EventsTable: () => null }));

import { ProjectStatus } from "./current-project";
import { IssueDetail } from "./issue-detail";

const org = { id: 1, name: "Acme", slug: "acme", tier: 0, createdAt: "" };
const project = {
  id: 7,
  name: "web-app",
  orgId: 1,
  platform: "javascript",
  slug: "web-app",
  createdAt: "",
};

const issue = {
  id: "018f9c4e-1a2b-7c3d-8e4f-0123456789ab",
  title: "TypeError: boom",
  culprit: "app.js",
  level: 4,
  status: 1,
  eventCount: 9,
  fingerprint: "abcdef012345aa",
  firstSeen: "2026-07-17T10:00:00Z",
  lastSeen: "2026-07-17T11:59:00Z",
  projectId: 7,
};

const event = {
  eventId: "e1",
  exceptionType: "TypeError",
  exceptionValue: "kaboom",
  level: "error",
  message: "boom",
  payload: '{"a":1}',
  timestamp: "2026-07-17T11:59:00.000Z",
};

afterEach(() => vi.clearAllMocks());

describe("IssueDetail", () => {
  it("shows not-found when the issue query errors (404)", () => {
    useCurrentProjectMock.mockReturnValue({ status: ProjectStatus.Ready, org, project });
    useGetIssueMock.mockReturnValue({ isPending: false, isError: true, data: undefined });
    renderWithIntl(<IssueDetail issueId="00000000-0000-7000-8000-000000000000" />);
    expect(screen.getByText(/does not exist/i)).toBeInTheDocument();
  });

  it("renders the issue header, the meta rail and its latest events", () => {
    useCurrentProjectMock.mockReturnValue({ status: ProjectStatus.Ready, org, project });
    const older = { ...event, eventId: "e2", exceptionValue: "older-kaboom" };
    useGetIssueMock.mockReturnValue({
      isPending: false,
      isError: false,
      data: { data: { issue, events: [event, older], frameLinks: [[], []] } },
    });
    renderWithIntl(<IssueDetail issueId="018f9c4e-1a2b-7c3d-8e4f-0123456789ab" />);

    expect(screen.getByRole("heading", { name: "TypeError: boom" })).toBeInTheDocument();
    expect(screen.getByText("unresolved")).toBeInTheDocument();
    // The newest event renders inline with its raw-payload button; older events now lazy-load in the
    // (stubbed here) compact EventsTable, so only the inline raw action is present.
    expect(screen.getAllByText("Raw payload").length).toBeGreaterThanOrEqual(1);
  });

  // The links are listed per event beside detail.events, so the evidence must read the list of the event
  // it shows: here the first event cannot be parsed and the second one is shown.
  it("links the shown event's frames through the server's frame links", () => {
    useCurrentProjectMock.mockReturnValue({ status: ProjectStatus.Ready, org, project });
    const unreadable = { ...event, eventId: "e0", payload: "not json" };
    const shown = {
      ...event,
      payload: JSON.stringify({
        Exceptions: [
          { Stacktrace: { Frames: [{ Filename: "src/cart.js", Lineno: 5, InApp: true }] } },
        ],
      }),
    };
    useGetIssueMock.mockReturnValue({
      isPending: false,
      isError: false,
      data: {
        data: {
          issue,
          events: [unreadable, shown],
          frameLinks: [
            [],
            [{ frame: 0, url: "https://github.com/acme/app/blob/main/src/cart.js#L5" }],
          ],
        },
      },
    });
    renderWithIntl(<IssueDetail issueId="018f9c4e-1a2b-7c3d-8e4f-0123456789ab" />);

    expect(screen.getByRole("link", { name: "src/cart.js:5" })).toHaveAttribute(
      "href",
      "https://github.com/acme/app/blob/main/src/cart.js#L5",
    );
  });

  it("shows the first release on the meta rail when the issue has one", () => {
    useCurrentProjectMock.mockReturnValue({ status: ProjectStatus.Ready, org, project });
    useGetIssueMock.mockReturnValue({
      isPending: false,
      isError: false,
      data: {
        data: { issue: { ...issue, firstRelease: "1.4.0" }, events: [event], frameLinks: [[]] },
      },
    });
    renderWithIntl(<IssueDetail issueId="018f9c4e-1a2b-7c3d-8e4f-0123456789ab" />);

    expect(screen.getByText("First release")).toBeInTheDocument();
    expect(screen.getByText("1.4.0")).toBeInTheDocument();
  });

  it("shows the agent block on the rail for an event made from an agent's failed span", () => {
    useCurrentProjectMock.mockReturnValue({ status: ProjectStatus.Ready, org, project });
    const agentEvent = {
      ...event,
      payload: JSON.stringify({
        Tags: { "gen_ai.operation.name": "execute_tool", "gen_ai.tool.name": "boom" },
      }),
    };
    useGetIssueMock.mockReturnValue({
      isPending: false,
      isError: false,
      data: { data: { issue, events: [agentEvent], frameLinks: [[]] } },
    });
    renderWithIntl(<IssueDetail issueId="018f9c4e-1a2b-7c3d-8e4f-0123456789ab" />);

    expect(screen.getByText("Agent failure")).toBeInTheDocument();
    expect(screen.getByText("Tool call")).toBeInTheDocument();
  });

  it("omits the first-release row when the issue has none", () => {
    useCurrentProjectMock.mockReturnValue({ status: ProjectStatus.Ready, org, project });
    useGetIssueMock.mockReturnValue({
      isPending: false,
      isError: false,
      data: { data: { issue, events: [event], frameLinks: [[]] } },
    });
    renderWithIntl(<IssueDetail issueId="018f9c4e-1a2b-7c3d-8e4f-0123456789ab" />);

    expect(screen.queryByText("First release")).not.toBeInTheDocument();
  });
});
