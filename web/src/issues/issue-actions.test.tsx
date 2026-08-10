import { fireEvent, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { renderWithIntl } from "@/src/test-utils/render-with-intl";

// The mutation and cache are faked; the toggle logic (which status each button sends) runs for real.
const mutate = vi.fn();
const assignMutate = vi.fn();
vi.mock("@/src/api/generated/condux", () => ({
  useUpdateIssueStatus: () => ({ mutate, isPending: false }),
  useAssignIssue: () => ({ mutate: assignMutate, isPending: false }),
  useListMembers: () => ({
    data: { data: [{ userId: 9, email: "kim@acme.dev", role: "member", createdAt: "" }] },
  }),
  getGetIssueQueryKey: () => ["issue"],
  getListIssuesQueryKey: () => ["issues"],
}));
vi.mock("@tanstack/react-query", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@tanstack/react-query")>()),
  useQueryClient: () => ({ invalidateQueries: vi.fn() }),
}));

import { IssueActions } from "./issue-actions";

afterEach(() => vi.clearAllMocks());

describe("IssueActions", () => {
  it("resolves an unresolved issue and offers unresolve when resolved", async () => {
    const first = renderWithIntl(
      <IssueActions projectId={7} issueId="i-1" status={1} orgId={1} assigneeUserId={null} />,
    );

    await userEvent.click(screen.getByRole("button", { name: "Resolve" }));
    expect(mutate).toHaveBeenCalledWith({ projectId: 7, issueId: "i-1", data: { status: 2 } });

    first.unmount();
    renderWithIntl(
      <IssueActions projectId={7} issueId="i-1" status={2} orgId={1} assigneeUserId={null} />,
    );
    await userEvent.click(screen.getByRole("button", { name: "Unresolve" }));
    expect(mutate).toHaveBeenCalledWith({ projectId: 7, issueId: "i-1", data: { status: 1 } });
  });

  it("ignores and unignores, and assigns a member from the menu", async () => {
    renderWithIntl(
      <IssueActions projectId={7} issueId="i-1" status={1} orgId={1} assigneeUserId={null} />,
    );

    await userEvent.click(screen.getByRole("button", { name: "Ignore" }));
    expect(mutate).toHaveBeenCalledWith({ projectId: 7, issueId: "i-1", data: { status: 3 } });

    await userEvent.click(screen.getByRole("button", { name: "Assign" }));
    await userEvent.click(await screen.findByRole("menuitem", { name: "kim@acme.dev" }));
    expect(assignMutate).toHaveBeenCalledWith({
      projectId: 7,
      issueId: "i-1",
      data: { userId: 9 },
    });
  });

  it("triages with the keyboard: e resolves, i ignores", () => {
    renderWithIntl(
      <IssueActions projectId={7} issueId="i-1" status={1} orgId={1} assigneeUserId={null} />,
    );
    fireEvent.keyDown(document.body, { key: "e" });
    expect(mutate).toHaveBeenCalledWith({ projectId: 7, issueId: "i-1", data: { status: 2 } });
    fireEvent.keyDown(document.body, { key: "i" });
    expect(mutate).toHaveBeenCalledWith({ projectId: 7, issueId: "i-1", data: { status: 3 } });
  });

  it("does not triage while typing in a field", () => {
    renderWithIntl(
      <IssueActions projectId={7} issueId="i-1" status={1} orgId={1} assigneeUserId={null} />,
    );
    const input = document.createElement("input");
    document.body.appendChild(input);
    fireEvent.keyDown(input, { key: "e" });
    expect(mutate).not.toHaveBeenCalled();
    input.remove();
  });
});
