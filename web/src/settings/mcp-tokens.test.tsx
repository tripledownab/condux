import { fireEvent, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { renderWithIntl } from "@/src/test-utils/render-with-intl";

vi.mock("@tanstack/react-query", () => ({
  useQueryClient: () => ({ invalidateQueries: vi.fn() }),
}));

const useListMcpTokensMock = vi.fn();
const createMutate = vi.fn();
const revokeMutate = vi.fn();
vi.mock("@/src/api/generated/condux", () => ({
  useListMcpTokens: () => useListMcpTokensMock(),
  useCreateMcpToken: () => ({ mutate: createMutate, isPending: false, isError: false }),
  useRevokeMcpToken: () => ({ mutate: revokeMutate, isPending: false }),
  getListMcpTokensQueryKey: () => ["mcp-tokens"],
}));

import { McpTokens } from "./mcp-tokens";

const active = {
  id: "t1",
  name: "Claude Desktop",
  createdAt: "2026-08-01T00:00:00Z",
  lastUsedAt: null,
  revoked: false,
};

beforeEach(() => {
  useListMcpTokensMock.mockReturnValue({ data: { data: [] }, isPending: false, isError: false });
});
afterEach(() => vi.clearAllMocks());

describe("McpTokens", () => {
  it("lists tokens with status and usage", () => {
    useListMcpTokensMock.mockReturnValue({
      data: {
        data: [
          { ...active, lastUsedAt: "2026-08-02T00:00:00Z" },
          { id: "t2", name: "Old agent", createdAt: "x", lastUsedAt: null, revoked: true },
        ],
      },
      isPending: false,
      isError: false,
    });
    renderWithIntl(<McpTokens projectId={7} canManage={false} />);

    expect(screen.getByText("Claude Desktop")).toBeInTheDocument();
    expect(screen.getByText("Old agent")).toBeInTheDocument();
    expect(screen.getByText(/Last used/)).toBeInTheDocument();
    expect(screen.getByText("Never used")).toBeInTheDocument();
    expect(screen.getByText("Revoked")).toBeInTheDocument();
  });

  it("mints a token and shows the raw value plus a connect snippet once", () => {
    createMutate.mockImplementation((_vars, options) =>
      options.onSuccess({
        status: 200,
        data: { id: "t1", name: "Claude", token: "condux_mcp_secret123", createdAt: "x" },
      }),
    );
    renderWithIntl(<McpTokens projectId={7} canManage />);

    fireEvent.change(screen.getByLabelText("Token name"), { target: { value: "Claude" } });
    fireEvent.click(screen.getByRole("button", { name: "Create token" }));

    expect(createMutate).toHaveBeenCalledWith(
      { projectId: 7, data: { name: "Claude" } },
      expect.objectContaining({ onSuccess: expect.any(Function) }),
    );
    // The raw token shows once in a copyable field, and the connect snippet embeds it as a bearer.
    expect(screen.getByDisplayValue("condux_mcp_secret123")).toBeInTheDocument();
    const snippet = document.querySelector("pre")?.textContent ?? "";
    expect(snippet).toContain("Bearer condux_mcp_secret123");
    expect(snippet).toContain("/api/mcp");
  });

  it("defaults the token name to Agent when left blank", () => {
    renderWithIntl(<McpTokens projectId={7} canManage />);
    fireEvent.click(screen.getByRole("button", { name: "Create token" }));

    expect(createMutate).toHaveBeenCalledWith(
      { projectId: 7, data: { name: "Agent" } },
      expect.anything(),
    );
  });

  it("is read-only for a non-admin (no create form, no revoke)", () => {
    useListMcpTokensMock.mockReturnValue({
      data: { data: [active] },
      isPending: false,
      isError: false,
    });
    renderWithIntl(<McpTokens projectId={7} canManage={false} />);

    expect(screen.queryByRole("button", { name: "Create token" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Revoke" })).not.toBeInTheDocument();
  });
});
