import { fireEvent, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { Org } from "@/src/api/generated/model";
import { renderWithIntl } from "@/src/test-utils/render-with-intl";

vi.mock("@tanstack/react-query", () => ({
  useQueryClient: () => ({ invalidateQueries: vi.fn() }),
}));

const useUpdate = vi.fn();
const useTest = vi.fn();
vi.mock("@/src/api/generated/condux", () => ({
  useUpdateWeeklySummarySettings: () => useUpdate(),
  useTestWeeklySummary: () => useTest(),
  getListMyOrgsQueryKey: () => ["orgs"],
}));

import { WeeklySummarySettings } from "./weekly-summary-settings";

const idle = { mutate: vi.fn(), isPending: false, isError: false };

function org(overrides: Partial<Org> = {}): Org {
  return {
    id: 3,
    name: "Acme",
    slug: "acme",
    tier: 2,
    createdAt: "",
    aiFixMode: 0,
    aiFixCostCapUsd: null,
    weeklySummaryEnabled: true,
    weeklySummaryDow: 1,
    weeklySummaryHour: 9,
    weeklySummaryTz: "UTC",
    ...overrides,
  } as Org;
}

beforeEach(() => {
  useUpdate.mockReturnValue(idle);
  useTest.mockReturnValue({ mutate: vi.fn() });
});

afterEach(() => vi.clearAllMocks());

describe("WeeklySummarySettings", () => {
  it("toggles the enable flag while preserving the current schedule", () => {
    const mutate = vi.fn();
    useUpdate.mockReturnValue({ ...idle, mutate });
    renderWithIntl(<WeeklySummarySettings org={org()} canManage />);

    fireEvent.click(screen.getByRole("checkbox"));
    expect(mutate).toHaveBeenCalledWith(
      { orgId: 3, data: { enabled: false, dayOfWeek: 1, hour: 9, timezone: "UTC" } },
      expect.objectContaining({ onSuccess: expect.any(Function) }),
    );
  });

  it("shows a read-only summary for a member (no controls)", () => {
    renderWithIntl(<WeeklySummarySettings org={org()} canManage={false} />);
    expect(screen.queryByRole("checkbox")).not.toBeInTheDocument();
    expect(screen.getByText(/Weekly summary is on, sent Monday at 09:00 UTC/)).toBeInTheDocument();
  });

  it("surfaces a save error", () => {
    useUpdate.mockReturnValue({ ...idle, isError: true });
    renderWithIntl(<WeeklySummarySettings org={org()} canManage />);
    expect(screen.getByRole("alert")).toBeInTheDocument();
  });
});
