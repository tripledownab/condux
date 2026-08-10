import { fireEvent, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { renderWithIntl } from "@/src/test-utils/render-with-intl";

vi.mock("@tanstack/react-query", () => ({
  useQueryClient: () => ({ invalidateQueries: vi.fn() }),
}));

const useListIssueNotesMock = vi.fn();
const createMutate = vi.fn();
const deleteMutate = vi.fn();
vi.mock("@/src/api/generated/condux", () => ({
  useListIssueNotes: () => useListIssueNotesMock(),
  useCreateIssueNote: () => ({ mutate: createMutate, isPending: false, isError: false }),
  useDeleteIssueNote: () => ({ mutate: deleteMutate, isPending: false }),
  getListIssueNotesQueryKey: () => ["notes"],
}));

import { IssueNotes } from "./issue-notes";

function note(over: Record<string, unknown> = {}) {
  return {
    id: "n1",
    body: "a note",
    authorUserId: 7,
    authorEmail: "me@condux.test",
    createdAt: "2026-07-30T00:00:00Z",
    ...over,
  };
}

function renderPanel(props: Partial<Parameters<typeof IssueNotes>[0]> = {}) {
  return renderWithIntl(
    <IssueNotes projectId={1} issueId="issue-1" currentUserId={7} canModerate={false} {...props} />,
  );
}

beforeEach(() => {
  createMutate.mockClear();
  deleteMutate.mockClear();
  useListIssueNotesMock.mockReturnValue({ data: { data: [] }, isPending: false });
});

describe("IssueNotes", () => {
  it("adds a note with a trimmed body", () => {
    renderPanel();
    fireEvent.change(screen.getByPlaceholderText("Add a note"), { target: { value: "  hello  " } });
    fireEvent.click(screen.getByRole("button", { name: "Add note" }));
    expect(createMutate).toHaveBeenCalledWith({
      projectId: 1,
      issueId: "issue-1",
      data: { body: "hello" },
    });
  });

  it("offers Delete only on the caller's own note when they are not a moderator", () => {
    useListIssueNotesMock.mockReturnValue({
      data: {
        data: [
          note({ id: "mine", authorUserId: 7 }),
          note({ id: "theirs", authorUserId: 99, authorEmail: "other@condux.test" }),
        ],
      },
      isPending: false,
    });
    renderPanel({ currentUserId: 7, canModerate: false });
    expect(screen.getAllByRole("button", { name: "Delete" })).toHaveLength(1);
  });

  it("offers Delete on every note to a moderator", () => {
    useListIssueNotesMock.mockReturnValue({
      data: { data: [note({ id: "a", authorUserId: 1 }), note({ id: "b", authorUserId: 2 })] },
      isPending: false,
    });
    renderPanel({ currentUserId: 999, canModerate: true });
    expect(screen.getAllByRole("button", { name: "Delete" })).toHaveLength(2);
  });
});
