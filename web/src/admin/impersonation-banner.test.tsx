import { fireEvent, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { renderWithIntl } from "@/src/test-utils/render-with-intl";

vi.mock("@tanstack/react-query", () => ({
  useQueryClient: () => ({ invalidateQueries: vi.fn() }),
}));

const meMock = vi.fn();
const stopMutate = vi.fn();
vi.mock("@/src/api/generated/condux", () => ({
  useMe: () => meMock(),
  useAdminStopImpersonation: () => ({ mutate: stopMutate, isPending: false }),
  getMeQueryKey: () => ["me"],
}));

import { ImpersonationBanner } from "./impersonation-banner";

afterEach(() => vi.clearAllMocks());

describe("ImpersonationBanner", () => {
  it("renders nothing when not impersonating", () => {
    meMock.mockReturnValue({ data: { data: { id: 1, email: "a@b.c", isPlatformAdmin: true } } });
    const { container } = renderWithIntl(<ImpersonationBanner />);
    expect(container).toBeEmptyDOMElement();
  });

  it("shows the org and exits on click", () => {
    meMock.mockReturnValue({
      data: {
        data: {
          id: 1,
          email: "a@b.c",
          isPlatformAdmin: true,
          impersonation: { orgId: 3, orgSlug: "acme", orgName: "Acme" },
        },
      },
    });
    renderWithIntl(<ImpersonationBanner />);

    expect(screen.getByText("Viewing as Acme (read-only)")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Exit" }));
    expect(stopMutate).toHaveBeenCalledOnce();
  });
});
