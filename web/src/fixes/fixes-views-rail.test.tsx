import { fireEvent, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { FixCostRollup, FixListItem } from "@/src/api/generated/model";
import { renderWithIntl } from "@/src/test-utils/render-with-intl";
import { FixBucket } from "./fix-views";
import { FixesViewsRail } from "./fixes-views-rail";

function item(status: number, verifyStatus: number): FixListItem {
  return {
    id: `f-${status}-${verifyStatus}`,
    issuePublicId: "p",
    issueTitle: "t",
    issueLevel: 4,
    status,
    verifyStatus,
    repoFullName: "acme/api",
    prUrl: "",
    summary: "",
    createdAt: "2026-07-22T10:00:00Z",
    updatedAt: "2026-07-22T10:00:00Z",
    viewed: true,
  };
}

afterEach(() => vi.clearAllMocks());

describe("FixesViewsRail", () => {
  const items = [item(2, 0), item(3, 0), item(3, 2)]; // Running, Ready, Held

  it("lists the buckets with counts over the active list", () => {
    renderWithIntl(
      <FixesViewsRail
        items={items}
        activeBucket={null}
        onSelectBucket={vi.fn()}
        archivedActive={false}
        onSelectArchived={vi.fn()}
        spend={null}
      />,
    );
    // "All" counts every fix; the "Ready for review" bucket has one.
    expect(screen.getByRole("button", { name: /All/ })).toHaveTextContent("3");
    expect(screen.getByRole("button", { name: /Ready for review/ })).toHaveTextContent("1");
  });

  it("selects a bucket on click", () => {
    const onSelectBucket = vi.fn();
    renderWithIntl(
      <FixesViewsRail
        items={items}
        activeBucket={null}
        onSelectBucket={onSelectBucket}
        archivedActive={false}
        onSelectArchived={vi.fn()}
        spend={null}
      />,
    );
    fireEvent.click(screen.getByRole("button", { name: /Ready for review/ }));
    expect(onSelectBucket).toHaveBeenCalledWith(FixBucket.Ready);
  });

  it("selects the archived view", () => {
    const onSelectArchived = vi.fn();
    renderWithIntl(
      <FixesViewsRail
        items={items}
        activeBucket={null}
        onSelectBucket={vi.fn()}
        archivedActive={false}
        onSelectArchived={onSelectArchived}
        spend={null}
      />,
    );
    fireEvent.click(screen.getByRole("button", { name: "Archived" }));
    expect(onSelectArchived).toHaveBeenCalled();
  });

  it("shows the spend total and a per-model breakdown, dashing an unpriced model", () => {
    const spend: FixCostRollup = {
      totalUsd: 17.5,
      runCount: 2,
      inputTokens: 1_200_000,
      outputTokens: 600_000,
      byModel: [
        {
          model: "claude-opus-4-8",
          runCount: 1,
          inputTokens: 1_000_000,
          outputTokens: 500_000,
          costUsd: 17.5,
        },
        {
          model: "gpt-4o",
          runCount: 1,
          inputTokens: 200_000,
          outputTokens: 100_000,
          costUsd: null,
        },
      ],
    };
    renderWithIntl(
      <FixesViewsRail
        items={items}
        activeBucket={null}
        onSelectBucket={vi.fn()}
        archivedActive={false}
        onSelectArchived={vi.fn()}
        spend={spend}
      />,
    );
    // $17.50 appears twice: the total, and the single priced model's row (they coincide with one model).
    expect(screen.getAllByText("$17.50")).toHaveLength(2);
    expect(screen.getByText("2 priced runs")).toBeInTheDocument();
    // The priced model shows its cost; the BYO model shows its token count instead of a dollar figure.
    expect(screen.getByText("claude-opus-4-8")).toBeInTheDocument();
    expect(screen.getByText("300K tok")).toBeInTheDocument();
  });

  it("hides the spend summary when no priced runs exist", () => {
    const spend: FixCostRollup = {
      totalUsd: 0,
      runCount: 0,
      inputTokens: 0,
      outputTokens: 0,
      byModel: [],
    };
    renderWithIntl(
      <FixesViewsRail
        items={items}
        activeBucket={null}
        onSelectBucket={vi.fn()}
        archivedActive={false}
        onSelectArchived={vi.fn()}
        spend={spend}
      />,
    );
    expect(screen.queryByText("Spend (30d)")).not.toBeInTheDocument();
  });
});
