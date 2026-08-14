import { fireEvent, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { renderWithIntl } from "@/src/test-utils/render-with-intl";

const providers = vi.fn();
vi.mock("./use-auth-providers", () => ({ useAuthProviders: () => providers() }));
vi.mock("@/src/api/fetcher", () => ({ apiUrl: (path: string) => `https://api.test${path}` }));

import { AlternativeSignIn } from "./alternative-sign-in";

afterEach(() => vi.clearAllMocks());

describe("AlternativeSignIn", () => {
  it("renders nothing when no provider is configured", () => {
    providers.mockReturnValue({ google: false, sso: false });

    const { container } = renderWithIntl(<AlternativeSignIn email="" />);

    expect(container.firstChild).toBeNull();
  });

  it("links to the Google start route when Google is enabled", () => {
    providers.mockReturnValue({ google: true, sso: false });

    renderWithIntl(<AlternativeSignIn email="" />);

    const link = screen.getByRole("link", { name: /continue with google/i });
    expect(link.getAttribute("href")).toBe("https://api.test/api/auth/oauth/google/start");
  });

  it("navigates to the SSO start route with the typed email", () => {
    providers.mockReturnValue({ google: false, sso: true });
    const assign = vi.fn();
    Object.defineProperty(window, "location", {
      value: { assign },
      writable: true,
      configurable: true,
    });

    renderWithIntl(<AlternativeSignIn email="dev@acme.test" />);
    fireEvent.click(screen.getByRole("button", { name: /sso/i }));

    expect(assign).toHaveBeenCalledWith(
      "https://api.test/api/auth/sso/start?email=dev%40acme.test",
    );
  });

  it("disables the SSO button until an email is entered", () => {
    providers.mockReturnValue({ google: false, sso: true });

    renderWithIntl(<AlternativeSignIn email="  " />);

    expect((screen.getByRole("button", { name: /sso/i }) as HTMLButtonElement).disabled).toBe(true);
  });

  it("says why the SSO button is disabled, and stops saying it once an email is typed", () => {
    providers.mockReturnValue({ google: false, sso: true });

    // The disabled button read as broken because the only explanation was a title tooltip, which a
    // disabled element never fires. Assert the reason is real text, and that it describes the button.
    // Unmount rather than rerender between the two states: rerender swaps the whole tree for what it is
    // given, which would drop the intl provider renderWithIntl wraps around it.
    const { unmount } = renderWithIntl(<AlternativeSignIn email="" />);

    const hint = screen.getByText(/enter your work email/i);
    expect(screen.getByRole("button", { name: /sso/i }).getAttribute("aria-describedby")).toBe(
      hint.id,
    );

    unmount();
    renderWithIntl(<AlternativeSignIn email="dev@acme.test" />);

    expect(screen.queryByText(/enter your work email/i)).toBeNull();
  });
});
