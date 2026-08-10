import { fireEvent, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { renderWithIntl } from "@/src/test-utils/render-with-intl";

const useListIssueEventsMock = vi.fn();
vi.mock("@/src/api/generated/condux", () => ({
  useListIssueEvents: (_projectId: number, _issueId: string, params: unknown) =>
    useListIssueEventsMock(params),
}));

import { EventsTable } from "./events-table";

function event(over: Record<string, unknown> = {}) {
  return {
    eventId: "e1",
    timestamp: "2026-07-30T00:00:00Z",
    level: "error",
    message: "boom",
    exceptionType: "TypeError",
    exceptionValue: "x is undefined",
    payload: "{}",
    ...over,
  };
}

beforeEach(() => useListIssueEventsMock.mockReset());

function renderTable(props: Partial<Parameters<typeof EventsTable>[0]> = {}) {
  return renderWithIntl(<EventsTable projectId={1} issueId="i-1" onShowRaw={vi.fn()} {...props} />);
}

describe("EventsTable", () => {
  it("renders nothing when the issue has no older events", () => {
    useListIssueEventsMock.mockReturnValue({
      data: { data: { events: [], hasMore: false } },
      isFetching: false,
    });
    const { container } = renderTable();
    expect(container).toBeEmptyDOMElement();
  });

  it("lists events and offers Load more only when there is more", () => {
    useListIssueEventsMock.mockReturnValue({
      data: {
        data: {
          events: [event({ eventId: "a" }), event({ eventId: "b", exceptionValue: "y is null" })],
          hasMore: true,
        },
      },
      isFetching: false,
    });
    renderTable();
    expect(screen.getByText("TypeError: x is undefined")).toBeInTheDocument();
    expect(screen.getByText("TypeError: y is null")).toBeInTheDocument();
    expect(screen.getAllByText("Raw")).toHaveLength(2);
    expect(screen.getByRole("button", { name: "Load more" })).toBeInTheDocument();
  });

  it("passes the event to onShowRaw when Raw is clicked", () => {
    const onShowRaw = vi.fn();
    const e = event({ eventId: "a" });
    useListIssueEventsMock.mockReturnValue({
      data: { data: { events: [e], hasMore: false } },
      isFetching: false,
    });
    renderTable({ onShowRaw });
    fireEvent.click(screen.getByText("Raw"));
    expect(onShowRaw).toHaveBeenCalledWith(e);
    expect(screen.queryByRole("button", { name: "Load more" })).not.toBeInTheDocument();
  });
});
