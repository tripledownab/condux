import { screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { ROUTES } from "@/src/routes";
import { renderWithIntl } from "@/src/test-utils/render-with-intl";

const replaceMock = vi.fn();
const pathnameMock = vi.fn();
vi.mock("next/navigation", () => ({
  useRouter: () => ({ replace: replaceMock }),
  usePathname: () => pathnameMock(),
}));

const useOnboardingMock = vi.fn();
vi.mock("./use-onboarding", () => ({
  useOnboarding: () => useOnboardingMock(),
}));

import { OnboardingGate } from "./onboarding-gate";

afterEach(() => vi.clearAllMocks());

describe("OnboardingGate", () => {
  it("redirects a tenant-less user to onboarding and hides the app", () => {
    pathnameMock.mockReturnValue(ROUTES.home);
    useOnboardingMock.mockReturnValue({ isPending: false, isComplete: false });
    renderWithIntl(
      <OnboardingGate>
        <div>app</div>
      </OnboardingGate>,
    );
    expect(replaceMock).toHaveBeenCalledWith(ROUTES.onboarding);
    expect(screen.queryByText("app")).not.toBeInTheDocument();
  });

  it("renders the app once onboarding is complete", () => {
    pathnameMock.mockReturnValue(ROUTES.home);
    useOnboardingMock.mockReturnValue({ isPending: false, isComplete: true });
    renderWithIntl(
      <OnboardingGate>
        <div>app</div>
      </OnboardingGate>,
    );
    expect(replaceMock).not.toHaveBeenCalled();
    expect(screen.getByText("app")).toBeInTheDocument();
  });

  it("always renders the onboarding page itself, even when incomplete", () => {
    pathnameMock.mockReturnValue(ROUTES.onboarding);
    useOnboardingMock.mockReturnValue({ isPending: false, isComplete: false });
    renderWithIntl(
      <OnboardingGate>
        <div>onboarding</div>
      </OnboardingGate>,
    );
    expect(replaceMock).not.toHaveBeenCalled();
    expect(screen.getByText("onboarding")).toBeInTheDocument();
  });

  it("shows a loading state while membership resolves off the onboarding route", () => {
    pathnameMock.mockReturnValue(ROUTES.home);
    useOnboardingMock.mockReturnValue({ isPending: true, isComplete: false });
    renderWithIntl(
      <OnboardingGate>
        <div>app</div>
      </OnboardingGate>,
    );
    expect(replaceMock).not.toHaveBeenCalled();
    expect(screen.queryByText("app")).not.toBeInTheDocument();
    expect(screen.getByText("Loading")).toBeInTheDocument();
  });
});
