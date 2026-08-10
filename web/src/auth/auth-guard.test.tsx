import { screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { renderWithIntl } from "@/src/test-utils/render-with-intl";

const replace = vi.fn();
vi.mock("next/navigation", () => ({ useRouter: () => ({ replace }) }));

const useMeMock = vi.fn();
vi.mock("@/src/api/generated/condux", () => ({ useMe: () => useMeMock() }));

import { AuthGuard } from "./auth-guard";

afterEach(() => vi.clearAllMocks());

describe("AuthGuard", () => {
  it("shows a loading state while the session resolves", () => {
    useMeMock.mockReturnValue({ isPending: true, isError: false });
    renderWithIntl(
      <AuthGuard>
        <div>secret</div>
      </AuthGuard>,
    );
    expect(screen.getByRole("status")).toBeInTheDocument();
    expect(screen.queryByText("secret")).not.toBeInTheDocument();
    expect(replace).not.toHaveBeenCalled();
  });

  it("renders children once authenticated", () => {
    useMeMock.mockReturnValue({ isPending: false, isError: false });
    renderWithIntl(
      <AuthGuard>
        <div>secret</div>
      </AuthGuard>,
    );
    expect(screen.getByText("secret")).toBeInTheDocument();
    expect(replace).not.toHaveBeenCalled();
  });

  it("redirects to /login when the session is missing (401)", () => {
    useMeMock.mockReturnValue({ isPending: false, isError: true });
    renderWithIntl(
      <AuthGuard>
        <div>secret</div>
      </AuthGuard>,
    );
    expect(replace).toHaveBeenCalledWith("/login");
    expect(screen.queryByText("secret")).not.toBeInTheDocument();
  });
});
