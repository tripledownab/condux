import { fireEvent, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ConduxApiError } from "@/src/api/fetcher";
import { renderWithIntl } from "@/src/test-utils/render-with-intl";

const useChangePasswordMock = vi.fn();
vi.mock("@/src/api/generated/condux", () => ({
  useChangePassword: () => useChangePasswordMock(),
}));

import { PasswordSettings } from "./password-settings";

const idle = { mutate: vi.fn(), isPending: false, isError: false, error: null };

function fill(current: string, next: string, confirm: string) {
  fireEvent.change(screen.getByLabelText("Current password"), { target: { value: current } });
  fireEvent.change(screen.getByLabelText("New password"), { target: { value: next } });
  fireEvent.change(screen.getByLabelText("Confirm new password"), { target: { value: confirm } });
}

beforeEach(() => useChangePasswordMock.mockReturnValue(idle));
afterEach(() => vi.clearAllMocks());

describe("PasswordSettings", () => {
  it("sends the current and new password once both fields agree", () => {
    const mutate = vi.fn();
    useChangePasswordMock.mockReturnValue({ ...idle, mutate });
    renderWithIntl(<PasswordSettings />);

    fill("old-password-1", "new-password-2", "new-password-2");
    fireEvent.click(screen.getByRole("button", { name: "Change password" }));

    expect(mutate).toHaveBeenCalledWith(
      { data: { currentPassword: "old-password-1", newPassword: "new-password-2" } },
      expect.objectContaining({ onSuccess: expect.any(Function) }),
    );
  });

  // Without a recovery path, a mistyped new password locks the account just as a mistyped signup does.
  it("refuses to submit when the confirmation does not match", () => {
    const mutate = vi.fn();
    useChangePasswordMock.mockReturnValue({ ...idle, mutate });
    renderWithIntl(<PasswordSettings />);

    fill("old-password-1", "new-password-2", "new-password-3");

    expect(screen.getByText("The two passwords do not match.")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Change password" })).toBeDisabled();
    fireEvent.click(screen.getByRole("button", { name: "Change password" }));
    expect(mutate).not.toHaveBeenCalled();
  });

  it("refuses a new password under the server's minimum", () => {
    renderWithIntl(<PasswordSettings />);
    fill("old-password-1", "short", "short");
    expect(screen.getByRole("button", { name: "Change password" })).toBeDisabled();
  });

  // A 401 here means the current password was wrong OR the account is federated and has none. Saying
  // only "wrong password" would leave a Google user retyping a password they never set.
  it("explains that a federated account has no password to change", () => {
    useChangePasswordMock.mockReturnValue({
      ...idle,
      isError: true,
      error: new ConduxApiError("POST", "/api/auth/password", 401, undefined),
    });
    renderWithIntl(<PasswordSettings />);

    expect(screen.getByText(/identity provider/)).toBeInTheDocument();
  });
});
