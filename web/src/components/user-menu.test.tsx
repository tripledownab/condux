import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { renderWithIntl } from "@/src/test-utils/render-with-intl";

// Only the data hooks are faked; the Radix dropdown itself runs for real, so this exercises the
// shadcn/ui integration (the trigger, opening, and the menuitem roles the primitive provides).
const useMeMock = vi.fn();
const logoutMutate = vi.fn();
vi.mock("@/src/api/generated/condux", () => ({
  useMe: () => useMeMock(),
  useLogout: () => ({ mutate: logoutMutate, isPending: false }),
  getMeQueryKey: () => ["me"],
}));
const replaceMock = vi.fn();
vi.mock("next/navigation", () => ({ useRouter: () => ({ replace: replaceMock }) }));
vi.mock("@tanstack/react-query", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@tanstack/react-query")>()),
  useQueryClient: () => ({ invalidateQueries: vi.fn() }),
}));

// The dev-only replay toggle reads this; default it off so it is absent (as in production).
const setReplayingMock = vi.fn();
const useReplayOnboardingMock = vi.fn();
vi.mock("@/src/onboarding/replay-onboarding", () => ({
  useReplayOnboarding: () => useReplayOnboardingMock(),
}));

import { ROUTES } from "@/src/routes";
import { UserMenu } from "./user-menu";

beforeEach(() => {
  useReplayOnboardingMock.mockReturnValue({
    available: false,
    replaying: false,
    setReplaying: setReplayingMock,
  });
});

afterEach(() => vi.clearAllMocks());

describe("UserMenu", () => {
  it("shows the email on the trigger and reveals sign out when opened", async () => {
    useMeMock.mockReturnValue({ data: { data: { email: "dev@condux.dev" } } });
    renderWithIntl(<UserMenu />);

    const trigger = screen.getByRole("button", { name: /account menu/i });
    expect(trigger).toHaveTextContent("dev@condux.dev");

    await userEvent.click(trigger);
    expect(await screen.findByRole("menuitem", { name: /sign out/i })).toBeInTheDocument();
  });

  it("collapses to the letter avatar in compact mode, menu still reachable", async () => {
    useMeMock.mockReturnValue({ data: { data: { email: "dev@condux.dev" } } });
    renderWithIntl(<UserMenu compact />);

    const trigger = screen.getByRole("button", { name: /account menu/i });
    expect(trigger).toHaveTextContent("D");
    expect(trigger).not.toHaveTextContent("dev@condux.dev");

    await userEvent.click(trigger);
    expect(await screen.findByRole("menuitem", { name: /sign out/i })).toBeInTheDocument();
  });

  it("triggers the logout mutation when sign out is selected", async () => {
    useMeMock.mockReturnValue({ data: { data: { email: "dev@condux.dev" } } });
    renderWithIntl(<UserMenu />);

    await userEvent.click(screen.getByRole("button", { name: /account menu/i }));
    await userEvent.click(await screen.findByRole("menuitem", { name: /sign out/i }));

    expect(logoutMutate).toHaveBeenCalledTimes(1);
  });

  it("hides the dev replay control outside development", async () => {
    useMeMock.mockReturnValue({ data: { data: { email: "dev@condux.dev" } } });
    renderWithIntl(<UserMenu />);

    await userEvent.click(screen.getByRole("button", { name: /account menu/i }));
    expect(screen.queryByRole("menuitem", { name: /replay onboarding/i })).not.toBeInTheDocument();
  });

  it("offers the dev replay control and re-engages the gate when selected", async () => {
    useMeMock.mockReturnValue({ data: { data: { email: "dev@condux.dev" } } });
    useReplayOnboardingMock.mockReturnValue({
      available: true,
      replaying: false,
      setReplaying: setReplayingMock,
    });
    renderWithIntl(<UserMenu />);

    await userEvent.click(screen.getByRole("button", { name: /account menu/i }));
    await userEvent.click(await screen.findByRole("menuitem", { name: /replay onboarding/i }));

    expect(setReplayingMock).toHaveBeenCalledWith(true);
    expect(replaceMock).toHaveBeenCalledWith(ROUTES.onboarding);
  });
});
