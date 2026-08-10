import { screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { FixListItem } from "@/src/api/generated/model";
import { renderWithIntl } from "@/src/test-utils/render-with-intl";

vi.mock("next/navigation", () => ({ useParams: () => ({ fixId: "f2" }) }));

import { FixesListRail } from "./fixes-list-rail";

function item(overrides: Partial<FixListItem>): FixListItem {
  return {
    id: "f1",
    issuePublicId: "0192f000-0000-7000-8000-000000000000",
    issueTitle: "TypeError: boom",
    issueLevel: 4,
    status: 3, // Succeeded
    verifyStatus: 0,
    repoFullName: "acme/api",
    prUrl: "",
    summary: "",
    createdAt: "2026-07-22T10:00:00Z",
    updatedAt: "2026-07-22T10:00:00Z",
    viewed: true,
    ...overrides,
  };
}

afterEach(() => vi.clearAllMocks());

describe("FixesListRail", () => {
  it("renders a row with the issue title, repo and status", () => {
    renderWithIntl(
      <FixesListRail heading="All · 1" isPending={false} isError={false} fixes={[item({})]} />,
    );
    expect(screen.getByText("TypeError: boom")).toBeInTheDocument();
    expect(screen.getByText("acme/api")).toBeInTheDocument();
    expect(screen.getByText("Draft PR ready")).toBeInTheDocument();
  });

  it("marks an unviewed fix and highlights the active row from the route", () => {
    renderWithIntl(
      <FixesListRail
        heading="All · 2"
        isPending={false}
        isError={false}
        fixes={[item({ id: "f1", viewed: false }), item({ id: "f2", viewed: true })]}
      />,
    );
    expect(screen.getByText("Unviewed")).toBeInTheDocument();
    // The route's fixId (f2) is the active row.
    const active = screen
      .getAllByRole("link")
      .find((l) => l.getAttribute("aria-current") === "page");
    expect(active).toHaveAttribute("href", "/fixes/f2");
  });

  it("shows the verification state instead of the run status once merged", () => {
    renderWithIntl(
      <FixesListRail
        heading="Verifying · 1"
        isPending={false}
        isError={false}
        fixes={[item({ verifyStatus: 1 })]}
      />,
    );
    expect(screen.getByText("Verifying in production")).toBeInTheDocument();
    expect(screen.queryByText("Draft PR ready")).not.toBeInTheDocument();
  });

  it("shows the empty state when there are no fixes", () => {
    renderWithIntl(
      <FixesListRail heading="All · 0" isPending={false} isError={false} fixes={[]} />,
    );
    expect(screen.getByText(/No fixes here yet/)).toBeInTheDocument();
  });
});
