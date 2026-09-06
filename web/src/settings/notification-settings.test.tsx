import { fireEvent, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { renderWithIntl } from "@/src/test-utils/render-with-intl";

vi.mock("@tanstack/react-query", () => ({
  useQueryClient: () => ({ invalidateQueries: vi.fn() }),
}));

const useListMock = vi.fn();
const useAddMock = vi.fn();
const useDeleteMock = vi.fn();
const useTestMock = vi.fn();
vi.mock("@/src/api/generated/condux", () => ({
  useListNotificationChannels: () => useListMock(),
  useAddNotificationChannel: () => useAddMock(),
  useDeleteNotificationChannel: () => useDeleteMock(),
  useTestNotificationChannel: () => useTestMock(),
  getListNotificationChannelsQueryKey: () => ["notification-channels"],
}));

const useCurrentOrgMock = vi.fn();
vi.mock("@/src/orgs/current-org", () => ({
  OrgStatus: { Loading: "loading", Error: "error", NoOrg: "no-org", Ready: "ready" },
  useCurrentOrg: () => useCurrentOrgMock(),
}));
// The weekly-summary section has its own test; stub it here so this suite stays focused on channels
// (and doesn't need the weekly-summary hooks mocked).
vi.mock("./weekly-summary-settings", () => ({ WeeklySummarySettings: () => null }));
vi.mock("./weekly-summary-subscription", () => ({ WeeklySummarySubscription: () => null }));

import { NotificationSettings } from "./notification-settings";

const webhook = { id: "chan-1", channel: 3, target: "https://hooks.test/w" };
const idle = { mutate: vi.fn(), isPending: false };

beforeEach(() => {
  useCurrentOrgMock.mockReturnValue({ status: "ready", org: { id: 7 }, role: "admin" });
  useListMock.mockReturnValue({ data: { data: [webhook] } });
  useAddMock.mockReturnValue(idle);
  useDeleteMock.mockReturnValue(idle);
  useTestMock.mockReturnValue(idle);
});

afterEach(() => vi.clearAllMocks());

describe("NotificationSettings", () => {
  it("lists the org's channels with their type and destination", () => {
    renderWithIntl(<NotificationSettings />);
    // "Webhook" also appears as a select option, so assert the unique destination + the labelled type.
    expect(screen.getByText("https://hooks.test/w")).toBeInTheDocument();
    expect(screen.getAllByText("Webhook").length).toBeGreaterThan(0);
  });

  it("adds a channel with the chosen type and destination", async () => {
    const mutate = vi.fn();
    useAddMock.mockReturnValue({ ...idle, mutate });
    renderWithIntl(<NotificationSettings />);

    // The channel type is a Combobox: open the trigger, then pick the option.
    await userEvent.click(screen.getByRole("combobox", { name: "Channel" }));
    await userEvent.click(await screen.findByRole("option", { name: "Slack" }));
    fireEvent.change(screen.getByLabelText("Target"), {
      target: { value: "https://hooks.slack.test/s" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Add channel" }));

    expect(mutate).toHaveBeenCalledWith(
      { orgId: 7, data: { channel: 2, target: "https://hooks.slack.test/s" } },
      expect.objectContaining({ onSuccess: expect.any(Function) }),
    );
  });

  it("sends a test to a channel", () => {
    const mutate = vi.fn();
    useTestMock.mockReturnValue({ ...idle, mutate });
    renderWithIntl(<NotificationSettings />);

    fireEvent.click(screen.getByRole("button", { name: "Send test" }));
    expect(mutate).toHaveBeenCalledWith(
      { orgId: 7, id: "chan-1" },
      expect.objectContaining({ onSuccess: expect.any(Function), onError: expect.any(Function) }),
    );
  });

  it("removes a channel", () => {
    const mutate = vi.fn();
    useDeleteMock.mockReturnValue({ ...idle, mutate });
    renderWithIntl(<NotificationSettings />);

    fireEvent.click(screen.getByRole("button", { name: "Remove" }));
    expect(mutate).toHaveBeenCalledWith(
      { orgId: 7, id: "chan-1" },
      expect.objectContaining({ onSuccess: expect.any(Function) }),
    );
  });

  it("is read-only for a non-admin (no add, remove or test controls)", () => {
    useCurrentOrgMock.mockReturnValue({ status: "ready", org: { id: 7 }, role: "member" });
    renderWithIntl(<NotificationSettings />);

    expect(screen.getByText("https://hooks.test/w")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Add channel" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Remove" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Send test" })).not.toBeInTheDocument();
  });

  it("shows an empty state when no channels are configured", () => {
    useListMock.mockReturnValue({ data: { data: [] } });
    renderWithIntl(<NotificationSettings />);
    expect(screen.getByText("No channels yet. Add one to get notified.")).toBeInTheDocument();
  });
});
