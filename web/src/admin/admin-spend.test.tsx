import { fireEvent, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { renderWithIntl } from "@/src/test-utils/render-with-intl";

const rollupMock = vi.fn();
const runsMock = vi.fn();
vi.mock("@/src/api/generated/condux", () => ({
  useAdminSpendRollup: () => rollupMock(),
  useAdminSpendRuns: () => runsMock(),
}));

import { AdminSpend } from "./admin-spend";

afterEach(() => vi.clearAllMocks());

describe("AdminSpend", () => {
  it("renders the rollup total and per-run drill-down, and filters runs by search", () => {
    rollupMock.mockReturnValue({
      isPending: false,
      isError: false,
      data: {
        data: {
          totalUsd: 37.5,
          byModel: [
            {
              model: "claude-opus-4-8",
              runCount: 3,
              inputTokens: 1000,
              outputTokens: 500,
              costUsd: 37.5,
            },
          ],
          byOrg: [
            {
              orgId: 1,
              orgName: "Acme",
              runCount: 3,
              inputTokens: 1000,
              outputTokens: 500,
              costUsd: 37.5,
            },
          ],
        },
      },
    });
    runsMock.mockReturnValue({
      isPending: false,
      isError: false,
      data: {
        data: [
          {
            id: "a",
            orgId: 1,
            orgName: "Acme",
            model: "claude-opus-4-8",
            kind: "issue_fix",
            status: 3,
            inputTokens: 1000,
            outputTokens: 500,
            costUsd: 30,
            createdAt: "2026-01-01T00:00:00Z",
          },
          {
            id: "b",
            orgId: 2,
            orgName: "Globex",
            model: "gpt-4o",
            kind: "cve_bump",
            status: 4,
            inputTokens: 200,
            outputTokens: 0,
            costUsd: null,
            createdAt: "2026-01-01T00:00:00Z",
          },
        ],
      },
    });
    renderWithIntl(<AdminSpend />);

    // $37.50 shows in the total stat and the by-model/by-org tables.
    expect(screen.getAllByText("$37.50").length).toBeGreaterThan(0);
    expect(screen.getByText("$30.00")).toBeInTheDocument(); // Acme's run cost, unique to its run row
    expect(screen.getByText("CVE bump")).toBeInTheDocument(); // Globex's run

    // Filtering the runs to "globex" drops the Acme run row; the Globex run stays.
    fireEvent.change(screen.getByPlaceholderText("Filter by org or model"), {
      target: { value: "globex" },
    });
    expect(screen.queryByText("$30.00")).not.toBeInTheDocument();
    expect(screen.getByText("CVE bump")).toBeInTheDocument();
  });
});
