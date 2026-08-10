import { render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

const replaceMock = vi.fn();
vi.mock("next/navigation", () => ({ useRouter: () => ({ replace: replaceMock }) }));

const useMeMock = vi.fn();
vi.mock("@/src/api/generated/condux", () => ({ useMe: () => useMeMock() }));
vi.mock("@/src/components/loading", () => ({ FullScreenLoading: () => <div>loading</div> }));

import { AdminGuard } from "./admin-guard";

afterEach(() => vi.clearAllMocks());

describe("AdminGuard", () => {
  it("renders children for a platform admin", () => {
    useMeMock.mockReturnValue({
      data: { data: { id: 1, email: "boss@acme.test", isPlatformAdmin: true } },
      isPending: false,
      isError: false,
    });
    render(
      <AdminGuard>
        <div>console</div>
      </AdminGuard>,
    );

    expect(screen.getByText("console")).toBeInTheDocument();
    expect(replaceMock).not.toHaveBeenCalled();
  });

  it("redirects a signed-in non-admin home and shows nothing", () => {
    useMeMock.mockReturnValue({
      data: { data: { id: 2, email: "user@acme.test", isPlatformAdmin: false } },
      isPending: false,
      isError: false,
    });
    render(
      <AdminGuard>
        <div>console</div>
      </AdminGuard>,
    );

    expect(screen.queryByText("console")).not.toBeInTheDocument();
    expect(replaceMock).toHaveBeenCalledWith("/");
  });
});
