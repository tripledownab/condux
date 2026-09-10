import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { renderWithIntl } from "@/src/test-utils/render-with-intl";

const replace = vi.fn();
vi.mock("next/navigation", () => ({ useRouter: () => ({ replace }) }));
vi.mock("@tanstack/react-query", () => ({
  useQueryClient: () => ({ invalidateQueries: vi.fn(), removeQueries: vi.fn() }),
}));

// The shape orval actually hands back, so the success path is exercised as it runs in the browser.
const SUCCESSFUL_LOGIN = { status: 200, data: { mfaRequired: false } };

const loginMutate = vi.fn();
const signupMutate = vi.fn();
vi.mock("@/src/api/generated/condux", () => ({
  getMeQueryKey: () => ["me"],
  useLogin: () => ({ mutate: loginMutate, isPending: false, isError: false }),
  useSignup: () => ({ mutate: signupMutate, isPending: false, isError: false }),
  // AuthForm renders MfaChallenge, which reaches for this. Mocked here rather than in the challenge's
  // own file because the module mock is whole-module: omitting it makes the import fail and the form
  // never renders, which surfaces as "replace was not called" rather than as a missing export.
  useVerifyMfa: () => ({ mutate: vi.fn(), isPending: false, isError: false }),
}));
// The Google button has its own test; stub it here so this suite doesn't need the react-query provider.
vi.mock("./alternative-sign-in", () => ({ AlternativeSignIn: () => null }));

import { AuthForm } from "./auth-form";
import { AuthMode } from "./auth-mode";

beforeEach(() => window.history.replaceState(null, "", "/"));
afterEach(() => {
  vi.clearAllMocks();
  window.history.replaceState(null, "", "/");
});

describe("AuthForm", () => {
  it("submits the typed credentials through the login mutation", async () => {
    renderWithIntl(<AuthForm mode={AuthMode.Login} />);

    await userEvent.type(screen.getByLabelText("Email"), "dev@condux.ai");
    await userEvent.type(screen.getByLabelText("Password"), "hunter2hunter");
    await userEvent.click(screen.getByRole("button", { name: "Sign in" }));

    expect(loginMutate).toHaveBeenCalledTimes(1);
    expect(loginMutate.mock.calls[0][0]).toEqual({
      data: { email: "dev@condux.ai", password: "hunter2hunter" },
    });
    expect(signupMutate).not.toHaveBeenCalled();
  });

  it("uses the signup mutation in signup mode", async () => {
    renderWithIntl(<AuthForm mode={AuthMode.Signup} />);

    await userEvent.type(screen.getByLabelText("Email"), "new@condux.ai");
    await userEvent.type(screen.getByLabelText("Password"), "hunter2hunter");
    await userEvent.type(screen.getByLabelText("Confirm password"), "hunter2hunter");
    await userEvent.click(screen.getByRole("button", { name: "Create account" }));

    expect(signupMutate).toHaveBeenCalledTimes(1);
    expect(loginMutate).not.toHaveBeenCalled();
  });

  // A mistyped password at signup is unrecoverable: there is no change-password for someone who cannot
  // sign in, and the email cannot be reused because signup answers 409. So the account must not be
  // created at all until the two agree.
  it("refuses to create an account when the confirmation does not match", async () => {
    renderWithIntl(<AuthForm mode={AuthMode.Signup} />);

    await userEvent.type(screen.getByLabelText("Email"), "new@condux.ai");
    await userEvent.type(screen.getByLabelText("Password"), "hunter2hunter");
    await userEvent.type(screen.getByLabelText("Confirm password"), "hunter2hunterr");

    expect(screen.getByText("The two passwords do not match.")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Create account" }));
    expect(signupMutate).not.toHaveBeenCalled();
  });

  // Login must not grow a confirmation field: it would be nonsense and would break sign-in.
  it("does not ask for a confirmation when signing in", () => {
    renderWithIntl(<AuthForm mode={AuthMode.Login} />);
    expect(screen.queryByLabelText("Confirm password")).not.toBeInTheDocument();
  });

  it("redirects to a safe next path after login", async () => {
    window.history.replaceState(null, "", `/login?next=${encodeURIComponent("/invite?token=abc")}`);
    loginMutate.mockImplementation((_vars, opts) => opts?.onSuccess?.(SUCCESSFUL_LOGIN));
    renderWithIntl(<AuthForm mode={AuthMode.Login} />);

    await userEvent.type(screen.getByLabelText("Email"), "dev@condux.ai");
    await userEvent.type(screen.getByLabelText("Password"), "hunter2hunter");
    await userEvent.click(screen.getByRole("button", { name: "Sign in" }));

    await waitFor(() => expect(replace).toHaveBeenCalledWith("/invite?token=abc"));
  });

  it("ignores an unsafe cross-origin next and falls back to home", async () => {
    window.history.replaceState(null, "", `/login?next=${encodeURIComponent("//evil.com")}`);
    loginMutate.mockImplementation((_vars, opts) => opts?.onSuccess?.(SUCCESSFUL_LOGIN));
    renderWithIntl(<AuthForm mode={AuthMode.Login} />);

    await userEvent.type(screen.getByLabelText("Email"), "dev@condux.ai");
    await userEvent.type(screen.getByLabelText("Password"), "hunter2hunter");
    await userEvent.click(screen.getByRole("button", { name: "Sign in" }));

    await waitFor(() => expect(replace).toHaveBeenCalledWith("/"));
  });

  // Clause 1 of the terms says creating an account accepts them, which only holds if the person was
  // shown them at that moment. Signup is where the contract forms, so that is where the notice goes.
  it("shows the acceptance notice on signup", () => {
    vi.stubEnv("NEXT_PUBLIC_CONDUX_LEGAL_URL", "https://condux.ai");
    renderWithIntl(<AuthForm mode={AuthMode.Signup} />);

    expect(screen.getByRole("link", { name: "Terms of Service" })).toHaveAttribute(
      "href",
      "https://condux.ai/terms",
    );
  });

  it("does not repeat the acceptance notice on login, which forms no contract", () => {
    vi.stubEnv("NEXT_PUBLIC_CONDUX_LEGAL_URL", "https://condux.ai");
    renderWithIntl(<AuthForm mode={AuthMode.Login} />);

    expect(screen.queryByRole("link", { name: "Terms of Service" })).not.toBeInTheDocument();
  });
});
