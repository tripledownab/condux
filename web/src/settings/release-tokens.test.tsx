import { fireEvent, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { renderWithIntl } from "@/src/test-utils/render-with-intl";

vi.mock("@tanstack/react-query", () => ({
  useQueryClient: () => ({ invalidateQueries: vi.fn() }),
}));

const useListReleaseTokensMock = vi.fn();
const createMutate = vi.fn();
const revokeMutate = vi.fn();
vi.mock("@/src/api/generated/condux", () => ({
  useListReleaseTokens: () => useListReleaseTokensMock(),
  useCreateReleaseToken: () => ({ mutate: createMutate, isPending: false, isError: false }),
  useRevokeReleaseToken: () => ({ mutate: revokeMutate, isPending: false }),
  getListReleaseTokensQueryKey: () => ["release-tokens"],
}));

import { ReleaseTokens } from "./release-tokens";

const active = {
  id: "t1",
  name: "GitHub Actions",
  createdAt: "2026-07-20T00:00:00Z",
  lastUsedAt: null,
  revoked: false,
};

beforeEach(() => {
  useListReleaseTokensMock.mockReturnValue({
    data: { data: [] },
    isPending: false,
    isError: false,
  });
});
afterEach(() => vi.clearAllMocks());

describe("ReleaseTokens", () => {
  it("lists tokens with status and usage", () => {
    useListReleaseTokensMock.mockReturnValue({
      data: {
        data: [
          { ...active, lastUsedAt: "2026-07-21T00:00:00Z" },
          {
            id: "t2",
            name: "Old CI",
            createdAt: "2026-07-01T00:00:00Z",
            lastUsedAt: null,
            revoked: true,
          },
        ],
      },
      isPending: false,
      isError: false,
    });
    renderWithIntl(<ReleaseTokens projectId={7} canManage={false} />);

    expect(screen.getByText("GitHub Actions")).toBeInTheDocument();
    expect(screen.getByText("Old CI")).toBeInTheDocument();
    expect(screen.getByText(/Last used/)).toBeInTheDocument();
    expect(screen.getByText("Never used")).toBeInTheDocument();
    expect(screen.getByText("Revoked")).toBeInTheDocument();
  });

  it("mints a token and shows the raw value once", () => {
    createMutate.mockImplementation((_vars, options) =>
      options.onSuccess({
        status: 200,
        data: {
          id: "t1",
          name: "CI",
          token: "condux_rel_secret123",
          createdAt: "2026-07-22T00:00:00Z",
        },
      }),
    );
    renderWithIntl(<ReleaseTokens projectId={7} canManage />);

    fireEvent.change(screen.getByLabelText("Token name"), { target: { value: "GitHub Actions" } });
    fireEvent.click(screen.getByRole("button", { name: "Create token" }));

    expect(createMutate).toHaveBeenCalledWith(
      { projectId: 7, data: { name: "GitHub Actions" } },
      expect.objectContaining({ onSuccess: expect.any(Function) }),
    );
    // The freshly-minted token is shown once, in a copyable field.
    expect(screen.getByDisplayValue("condux_rel_secret123")).toBeInTheDocument();
  });

  it("defaults the token name to CI when left blank", () => {
    renderWithIntl(<ReleaseTokens projectId={7} canManage />);
    fireEvent.click(screen.getByRole("button", { name: "Create token" }));

    expect(createMutate).toHaveBeenCalledWith(
      { projectId: 7, data: { name: "CI" } },
      expect.anything(),
    );
  });

  it("revokes an active token", () => {
    useListReleaseTokensMock.mockReturnValue({
      data: { data: [active] },
      isPending: false,
      isError: false,
    });
    renderWithIntl(<ReleaseTokens projectId={7} canManage />);

    fireEvent.click(screen.getByRole("button", { name: "Revoke" }));

    expect(revokeMutate).toHaveBeenCalledWith(
      { projectId: 7, tokenId: "t1" },
      expect.objectContaining({ onSuccess: expect.any(Function) }),
    );
  });

  it("is read-only for a non-admin (no create form, no revoke)", () => {
    useListReleaseTokensMock.mockReturnValue({
      data: { data: [active] },
      isPending: false,
      isError: false,
    });
    renderWithIntl(<ReleaseTokens projectId={7} canManage={false} />);

    expect(screen.queryByRole("button", { name: "Create token" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Revoke" })).not.toBeInTheDocument();
  });
});
