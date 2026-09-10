import { fireEvent, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { renderWithIntl } from "@/src/test-utils/render-with-intl";

const useResetPasswordMock = vi.fn();
const useForgotPasswordMock = vi.fn();
vi.mock("@/src/api/generated/condux", () => ({
  useResetPassword: () => useResetPasswordMock(),
  useForgotPassword: () => useForgotPasswordMock(),
}));

import { ForgotPasswordForm } from "./forgot-password-form";
import { ResetPasswordForm } from "./reset-password-form";

const idle = { mutate: vi.fn(), isPending: false, isError: false, isSuccess: false, error: null };

beforeEach(() => {
  useResetPasswordMock.mockReturnValue(idle);
  useForgotPasswordMock.mockReturnValue(idle);
  window.history.replaceState(null, "", "/reset");
});

afterEach(() => vi.clearAllMocks());

describe("ResetPasswordForm", () => {
  function fill(password: string, confirm: string) {
    fireEvent.change(screen.getByLabelText("New password"), { target: { value: password } });
    fireEvent.change(screen.getByLabelText("Confirm new password"), { target: { value: confirm } });
  }

  it("sends the token from the link with the new password", () => {
    const mutate = vi.fn();
    useResetPasswordMock.mockReturnValue({ ...idle, mutate });
    window.history.replaceState(null, "", "/reset?token=the-emailed-token");
    renderWithIntl(<ResetPasswordForm />);

    fill("brand-new-pass", "brand-new-pass");
    fireEvent.click(screen.getByRole("button", { name: "Set password" }));

    expect(mutate).toHaveBeenCalledWith({
      data: { token: "the-emailed-token", newPassword: "brand-new-pass" },
    });
  });

  // Whoever reaches this page has already lost one password. A mistyped replacement would send them
  // straight back to the same dead end.
  it("refuses to submit when the confirmation does not match", () => {
    const mutate = vi.fn();
    useResetPasswordMock.mockReturnValue({ ...idle, mutate });
    window.history.replaceState(null, "", "/reset?token=the-emailed-token");
    renderWithIntl(<ResetPasswordForm />);

    fill("brand-new-pass", "brand-new-pasz");

    expect(screen.getByText("The two passwords do not match.")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Set password" }));
    expect(mutate).not.toHaveBeenCalled();
  });

  it("says so when the link carries no token, instead of showing a form that cannot work", () => {
    renderWithIntl(<ResetPasswordForm />);
    expect(screen.getByText(/missing its token/)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Set password" })).not.toBeInTheDocument();
  });

  // Success must point at sign-in, not at the app: the reset deliberately does not create a session, so
  // any account with a second factor is still challenged for it.
  it("sends the user to sign in rather than into the app", () => {
    useResetPasswordMock.mockReturnValue({ ...idle, isSuccess: true });
    window.history.replaceState(null, "", "/reset?token=the-emailed-token");
    renderWithIntl(<ResetPasswordForm />);

    expect(screen.getByRole("link", { name: "Go to sign in" })).toHaveAttribute("href", "/login");
  });
});

describe("ForgotPasswordForm", () => {
  // The confirmation must not depend on whether the address exists, or the page becomes a way to test
  // which addresses are customers.
  it("confirms without revealing whether the address has an account", () => {
    useForgotPasswordMock.mockReturnValue({ ...idle, isSuccess: true });
    renderWithIntl(<ForgotPasswordForm />);

    expect(screen.getByText(/If that address has a Condux account/)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Send reset link" })).not.toBeInTheDocument();
  });

  it("submits the address", () => {
    const mutate = vi.fn();
    useForgotPasswordMock.mockReturnValue({ ...idle, mutate });
    renderWithIntl(<ForgotPasswordForm />);

    fireEvent.change(screen.getByLabelText("Email"), { target: { value: "someone@condux.ai" } });
    fireEvent.click(screen.getByRole("button", { name: "Send reset link" }));

    expect(mutate).toHaveBeenCalledWith({ data: { email: "someone@condux.ai" } });
  });
});
