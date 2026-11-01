import { fireEvent, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { TicketFailure, TicketState } from "@/src/linear/ticket-state";
import { renderWithIntl } from "@/src/test-utils/render-with-intl";

vi.mock("@tanstack/react-query", () => ({
  useQueryClient: () => ({ invalidateQueries: vi.fn() }),
}));

const linearMock = vi.fn();
const ticketMock = vi.fn();
const createMutate = vi.fn();
const retryMutate = vi.fn();
const unlinkMutate = vi.fn();
vi.mock("@/src/api/generated/condux", () => ({
  useGetLinearProject: () => linearMock(),
  useGetIssueTicket: () => ticketMock(),
  useCreateIssueTicket: () => ({ mutate: createMutate, isPending: false, isError: false }),
  useRetryIssueTicket: () => ({ mutate: retryMutate, isPending: false }),
  useUnlinkIssueTicket: () => ({ mutate: unlinkMutate, isPending: false }),
  getGetIssueTicketQueryKey: () => ["ticket"],
}));

import { IssueTicket } from "./issue-ticket";

const MAPPED_WITH = (autoCreate: boolean) => ({
  data: {
    status: 200,
    data: {
      connected: true,
      broken: false,
      workspaceName: "Acme",
      mapping: {
        teamId: "t1",
        teamName: "Engineering",
        needsAttention: false,
        autoCreate,
        labels: [],
      },
    },
  },
});
const MAPPED = MAPPED_WITH(false);

function ticket(over: Record<string, unknown>) {
  return {
    data: {
      status: 200,
      data: {
        id: "t",
        provider: 1,
        state: TicketState.Created,
        failure: null,
        key: null,
        url: null,
        ...over,
      },
    },
    isPending: false,
  };
}

const NO_TICKET = { data: { status: 204, data: undefined }, isPending: false };

function renderAction() {
  return renderWithIntl(<IssueTicket projectId={1} issueId="i1" issueTitle="TypeError: x" />);
}

beforeEach(() => {
  for (const mock of [createMutate, retryMutate, unlinkMutate]) {
    mock.mockClear();
  }
  linearMock.mockReturnValue(MAPPED);
  ticketMock.mockReturnValue(NO_TICKET);
});

describe("IssueTicket", () => {
  it("says when the auto-create setting opened the ticket", () => {
    ticketMock.mockReturnValue(
      ticket({
        state: TicketState.Created,
        key: "ENG-9",
        url: "https://linear.test/i/ENG-9",
        automatic: true,
      }),
    );
    renderAction();
    expect(screen.getByText("Opened automatically")).toBeInTheDocument();
  });

  // A warning in a project with auto-create on gets no ticket on its own, and the rail says why.
  // A statement of the rule, shown wherever auto-create is on and the issue has no ticket, since an issue's
  // level can change after its first event and a claim about why this one has none could be wrong.
  it("states the auto-create rule where auto-create is on and the issue has no ticket", () => {
    linearMock.mockReturnValue(MAPPED_WITH(true));
    renderAction();
    expect(screen.getByText(/only for a new issue at error or fatal level/)).toBeInTheDocument();

    linearMock.mockReturnValue(MAPPED_WITH(false));
    renderAction();
    expect(screen.getAllByText(/only for a new issue at error or fatal level/)).toHaveLength(1);
  });

  // Without a team there is nowhere for a ticket to go, and without Linear there is no Linear.
  it("renders nothing until the project has a Linear team", () => {
    linearMock.mockReturnValue({
      data: { status: 200, data: { connected: true, broken: false, mapping: null } },
    });
    const { container } = renderAction();
    expect(container).toBeEmptyDOMElement();

    linearMock.mockReturnValue({ data: undefined, error: { status: 404 } });
    expect(renderAction().container).toBeEmptyDOMElement();
  });

  // A ticket made before the team was unmapped, or before Linear stopped accepting the grant, is still a
  // real ticket, so its link stays. Only creating a new one needs a team.
  it("keeps showing an existing ticket after the team is unmapped or the connection breaks", () => {
    ticketMock.mockReturnValue(
      ticket({ state: TicketState.Created, key: "ENG-7", url: "https://linear.test/i/ENG-7" }),
    );
    linearMock.mockReturnValue({
      data: { status: 200, data: { connected: true, broken: true, mapping: null } },
    });
    renderAction();
    expect(screen.getByRole("link", { name: "ENG-7" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Create Linear issue" })).not.toBeInTheDocument();
  });

  it("names the team and the title before sending anything to Linear", () => {
    renderAction();
    fireEvent.click(screen.getByRole("button", { name: "Create Linear issue" }));
    expect(createMutate).not.toHaveBeenCalled();
    expect(screen.getByText(/Engineering/)).toHaveTextContent("TypeError: x");

    fireEvent.click(screen.getByRole("button", { name: "Create issue" }));
    expect(createMutate).toHaveBeenCalledWith({ projectId: 1, issueId: "i1" }, expect.anything());
  });

  it("links a created ticket by its identifier", () => {
    ticketMock.mockReturnValue(
      ticket({ state: TicketState.Created, key: "ENG-12", url: "https://linear.test/i/ENG-12" }),
    );
    renderAction();
    expect(screen.getByRole("link", { name: "ENG-12" })).toHaveAttribute(
      "href",
      "https://linear.test/i/ENG-12",
    );
  });

  it("says it is creating while the worker has the ticket", () => {
    ticketMock.mockReturnValue(ticket({ state: TicketState.Claimed }));
    renderAction();
    expect(screen.getByText("Creating the Linear issue…")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Retry" })).not.toBeInTheDocument();
  });

  // Uncertain is not a failure: the ticket may exist, so the message sends the person to look first.
  it("tells someone to check Linear before retrying an uncertain ticket", () => {
    ticketMock.mockReturnValue(ticket({ state: TicketState.Uncertain }));
    renderAction();
    expect(screen.getByText(/Check Linear before retrying/)).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Retry" }));
    expect(retryMutate).toHaveBeenCalledWith({ projectId: 1, issueId: "i1" }, expect.anything());
  });

  it("explains a failure by its reason and offers to unlink", () => {
    ticketMock.mockReturnValue(
      ticket({ state: TicketState.Failed, failure: TicketFailure.TeamUnavailable }),
    );
    renderAction();
    expect(screen.getByText(/lost access to it/)).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Unlink" }));
    expect(unlinkMutate).toHaveBeenCalled();
  });
});
