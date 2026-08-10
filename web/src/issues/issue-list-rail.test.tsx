import { fireEvent } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { issueDetailPath } from "@/src/routes";
import { renderWithIntl } from "@/src/test-utils/render-with-intl";
import { IssueSort } from "./issue-sort";

const pushMock = vi.fn();
const paramsMock = vi.fn();
vi.mock("next/navigation", () => ({
  useParams: () => paramsMock(),
  useRouter: () => ({ push: pushMock }),
}));

import { IssueListRail } from "./issue-list-rail";

function issue(id: string) {
  return {
    id,
    title: `Issue ${id}`,
    culprit: "app.js",
    level: 4,
    status: 1,
    eventCount: 1,
    fingerprint: `fp-${id}`,
    firstSeen: "2026-07-30T00:00:00Z",
    lastSeen: "2026-07-30T00:00:00Z",
    projectId: 1,
    assigneeUserId: null,
    firstRelease: null,
  };
}

function renderRail(activeId: string | null) {
  paramsMock.mockReturnValue(activeId ? { issueId: activeId } : {});
  return renderWithIntl(
    <IssueListRail
      heading="Issues"
      isPending={false}
      isError={false}
      issues={[issue("a"), issue("b"), issue("c")]}
      query=""
      onQueryChange={vi.fn()}
      sort={IssueSort.LastSeen}
      onSortChange={vi.fn()}
      hasMore={false}
      loadingMore={false}
      onLoadMore={vi.fn()}
    />,
  );
}

beforeEach(() => pushMock.mockClear());

describe("IssueListRail keyboard navigation", () => {
  it("j opens the next issue and k the previous, computed from the active row", () => {
    renderRail("b");
    fireEvent.keyDown(document.body, { key: "j" });
    expect(pushMock).toHaveBeenLastCalledWith(issueDetailPath("c"));
    fireEvent.keyDown(document.body, { key: "k" });
    expect(pushMock).toHaveBeenLastCalledWith(issueDetailPath("a"));
  });

  it("clamps at the ends (no wrap)", () => {
    renderRail("c");
    fireEvent.keyDown(document.body, { key: "j" }); // already last → stays on c
    expect(pushMock).toHaveBeenLastCalledWith(issueDetailPath("c"));
  });

  it("j from no selection opens the first issue", () => {
    renderRail(null);
    fireEvent.keyDown(document.body, { key: "j" });
    expect(pushMock).toHaveBeenLastCalledWith(issueDetailPath("a"));
  });

  it("does not navigate while typing in the search field", () => {
    const { container } = renderRail("b");
    const search = container.querySelector('input[type="search"]');
    fireEvent.keyDown(search ?? document.body, { key: "j" });
    expect(pushMock).not.toHaveBeenCalled();
  });

  // The search and sort controls used to scroll away with the list, because the whole rail was the
  // scroll container. jsdom does no layout, so assert the structure that produces the behaviour:
  // the list scrolls inside something the controls sit outside of.
  it("scrolls the list without taking the filters with it", () => {
    const { container } = renderRail("a");
    const search = container.querySelector('input[type="search"]');
    const list = container.querySelector("ul");

    expect(list?.closest(".overflow-y-auto")).not.toBeNull();
    expect(search?.closest(".overflow-y-auto")).toBeNull();
  });
});
