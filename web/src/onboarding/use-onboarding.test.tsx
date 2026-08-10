import { render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

const useMeMock = vi.fn();
vi.mock("@/src/api/generated/condux", () => ({
  useMe: () => useMeMock(),
}));

const useReplayOnboardingMock = vi.fn();
vi.mock("./replay-onboarding", () => ({
  useReplayOnboarding: () => useReplayOnboardingMock(),
}));

import { useOnboarding } from "./use-onboarding";

// A probe renders the hook's result as text so we can assert both flags without renderHook.
function Probe() {
  const { isPending, isComplete } = useOnboarding();
  return (
    <div data-testid="state">{`${isPending ? "pending" : "settled"}:${isComplete ? "complete" : "incomplete"}`}</div>
  );
}

const state = () => screen.getByTestId("state").textContent;

function me(value: { isPending?: boolean; isError?: boolean; onboarded?: boolean }) {
  useMeMock.mockReturnValue({
    isPending: value.isPending ?? false,
    isError: value.isError ?? false,
    data: value.onboarded === undefined ? undefined : { data: { onboarded: value.onboarded } },
  });
}

afterEach(() => vi.clearAllMocks());

describe("useOnboarding", () => {
  it("is pending while /me is still loading", () => {
    useReplayOnboardingMock.mockReturnValue({ replaying: false });
    me({ isPending: true });
    render(<Probe />);
    expect(state()).toBe("pending:incomplete");
  });

  it("is incomplete when the user is not yet onboarded", () => {
    useReplayOnboardingMock.mockReturnValue({ replaying: false });
    me({ onboarded: false });
    render(<Probe />);
    expect(state()).toBe("settled:incomplete");
  });

  it("is complete once the user is onboarded", () => {
    useReplayOnboardingMock.mockReturnValue({ replaying: false });
    me({ onboarded: true });
    render(<Probe />);
    expect(state()).toBe("settled:complete");
  });

  it("does not trap a user when /me errors", () => {
    useReplayOnboardingMock.mockReturnValue({ replaying: false });
    me({ isError: true });
    render(<Probe />);
    expect(state()).toBe("settled:complete");
  });

  it("forces incomplete while the dev replay flag is on, even for an onboarded user", () => {
    useReplayOnboardingMock.mockReturnValue({ replaying: true });
    me({ onboarded: true });
    render(<Probe />);
    expect(state()).toBe("settled:incomplete");
  });
});
