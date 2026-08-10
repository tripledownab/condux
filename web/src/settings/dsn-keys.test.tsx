import { fireEvent, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { renderWithIntl } from "@/src/test-utils/render-with-intl";

vi.mock("@tanstack/react-query", () => ({
  useQueryClient: () => ({ invalidateQueries: vi.fn() }),
}));

const useListKeysMock = vi.fn();
const createKeyMutate = vi.fn();
const updateKeyMutate = vi.fn();
const revokeKeyMutate = vi.fn();
vi.mock("@/src/api/generated/condux", () => ({
  useListKeys: () => useListKeysMock(),
  useCreateKey: () => ({ mutate: createKeyMutate, isPending: false }),
  useUpdateKey: () => ({ mutate: updateKeyMutate, isPending: false }),
  useRevokeKey: () => ({ mutate: revokeKeyMutate, isPending: false }),
  getListKeysQueryKey: () => ["keys"],
}));

import { DsnKeys } from "./dsn-keys";

function activeKey(overrides: Record<string, unknown> = {}) {
  return {
    id: 5,
    projectId: 7,
    publicKey: "pubkey-abc",
    label: "default",
    isActive: true,
    createdAt: "2026-01-01T00:00:00Z",
    revokedAt: null,
    ...overrides,
  };
}

afterEach(() => vi.clearAllMocks());

describe("DsnKeys", () => {
  it("creates a key with the name entered in the modal", async () => {
    useListKeysMock.mockReturnValue({ isPending: false, isError: false, data: { data: [] } });
    renderWithIntl(<DsnKeys projectId={7} publicId="uuid-x" />);

    fireEvent.click(screen.getByRole("button", { name: "New key" }));
    fireEvent.change(await screen.findByLabelText("Name"), { target: { value: "Production" } });
    fireEvent.click(screen.getByRole("button", { name: "Create key" }));

    expect(createKeyMutate).toHaveBeenCalledWith(
      { projectId: 7, data: { label: "Production" } },
      expect.objectContaining({ onSuccess: expect.any(Function) }),
    );
  });

  it("renames a key via the modal, prefilled with its current name", async () => {
    useListKeysMock.mockReturnValue({
      isPending: false,
      isError: false,
      data: { data: [activeKey({ label: "default" })] },
    });
    renderWithIntl(<DsnKeys projectId={7} publicId="uuid-x" />);

    // The row's Rename opens the modal (prefilled); the modal confirms with Save.
    fireEvent.click(screen.getByRole("button", { name: "Rename" }));
    fireEvent.change(await screen.findByDisplayValue("default"), {
      target: { value: "CI server" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    expect(updateKeyMutate).toHaveBeenCalledWith(
      { projectId: 7, keyId: 5, data: { label: "CI server" } },
      expect.objectContaining({ onSuccess: expect.any(Function) }),
    );
  });

  it("does not offer Rename on a revoked key", () => {
    useListKeysMock.mockReturnValue({
      isPending: false,
      isError: false,
      data: { data: [activeKey({ isActive: false, revokedAt: "2026-02-01T00:00:00Z" })] },
    });
    renderWithIntl(<DsnKeys projectId={7} publicId="uuid-x" />);

    expect(screen.queryByRole("button", { name: "Rename" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Revoke" })).not.toBeInTheDocument();
  });
});
