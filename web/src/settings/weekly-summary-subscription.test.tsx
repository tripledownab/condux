import { fireEvent, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

vi.mock("@tanstack/react-query", () => ({
  useQueryClient: () => ({ invalidateQueries: vi.fn() }),
}));

const useUpdate = vi.fn();
const useMeMock = vi.fn();
vi.mock("@/src/api/generated/condux", () => ({
  useMe: () => useMeMock(),
  useUpdateWeeklySummarySubscription: () => useUpdate(),
  getMeQueryKey: () => ["me"],
}));

import { renderWithIntl } from "@/src/test-utils/render-with-intl";
import { WeeklySummarySubscription } from "./weekly-summary-subscription";

const me = (weeklySummaryOptOut: boolean) => ({ data: { data: { weeklySummaryOptOut } } });
const mutation = (mutate = vi.fn()) => ({ mutate, isPending: false, isError: false });

describe("WeeklySummarySubscription", () => {
  it("is checked when the user has not opted out", () => {
    useMeMock.mockReturnValue(me(false));
    useUpdate.mockReturnValue(mutation());
    renderWithIntl(<WeeklySummarySubscription />);
    expect(screen.getByRole("checkbox")).toBeChecked();
  });

  it("is unchecked when the user has opted out", () => {
    useMeMock.mockReturnValue(me(true));
    useUpdate.mockReturnValue(mutation());
    renderWithIntl(<WeeklySummarySubscription />);
    expect(screen.getByRole("checkbox")).not.toBeChecked();
  });

  // The checkbox is the inverse of what is stored, so unticking it must send optOut TRUE. Getting this
  // backwards would silently unsubscribe everyone who tried to subscribe, which is why both directions
  // are asserted, each from its own render rather than two components left mounted in one test.
  it("sends optOut true when a subscribed user unticks it", () => {
    const mutate = vi.fn();
    useMeMock.mockReturnValue(me(false));
    useUpdate.mockReturnValue(mutation(mutate));
    renderWithIntl(<WeeklySummarySubscription />);

    fireEvent.click(screen.getByRole("checkbox"));

    expect(mutate).toHaveBeenCalledWith({ data: { optOut: true } }, expect.anything());
  });

  it("sends optOut false when an opted-out user re-ticks it", () => {
    const mutate = vi.fn();
    useMeMock.mockReturnValue(me(true));
    useUpdate.mockReturnValue(mutation(mutate));
    renderWithIntl(<WeeklySummarySubscription />);

    fireEvent.click(screen.getByRole("checkbox"));

    expect(mutate).toHaveBeenCalledWith({ data: { optOut: false } }, expect.anything());
  });
});
