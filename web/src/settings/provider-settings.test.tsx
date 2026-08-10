import { fireEvent, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ConduxApiError } from "@/src/api/fetcher";
import { OrgStatus } from "@/src/orgs/current-org";
import { renderWithIntl } from "@/src/test-utils/render-with-intl";

vi.mock("@tanstack/react-query", () => ({
  useQueryClient: () => ({ invalidateQueries: vi.fn() }),
}));

const useGetLlmConfigMock = vi.fn();
const useSetLlmConfigMock = vi.fn();
const useDeleteLlmConfigMock = vi.fn();
const useListLlmModelsMock = vi.fn();
vi.mock("@/src/api/generated/condux", () => ({
  useGetLlmConfig: () => useGetLlmConfigMock(),
  useSetLlmConfig: () => useSetLlmConfigMock(),
  useDeleteLlmConfig: () => useDeleteLlmConfigMock(),
  useListLlmModels: () => useListLlmModelsMock(),
  getGetLlmConfigQueryKey: () => ["llm"],
}));

const useCurrentOrgMock = vi.fn();
vi.mock("@/src/orgs/current-org", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/src/orgs/current-org")>()),
  useCurrentOrg: () => useCurrentOrgMock(),
}));

import { ProviderSettings } from "./provider-settings";

const idleSave = { mutate: vi.fn(), isPending: false, isError: false, error: null };
const idleRemove = { mutate: vi.fn(), isPending: false };
const idleModels = { mutate: vi.fn(), isPending: false, isError: false };

function admin() {
  useCurrentOrgMock.mockReturnValue({
    status: OrgStatus.Ready,
    org: { id: 3, tier: 3 },
    role: "admin",
  });
}

beforeEach(() => {
  admin();
  useGetLlmConfigMock.mockReturnValue({ isPending: false, data: undefined });
  useSetLlmConfigMock.mockReturnValue(idleSave);
  useDeleteLlmConfigMock.mockReturnValue(idleRemove);
  useListLlmModelsMock.mockReturnValue(idleModels);
});

afterEach(() => vi.clearAllMocks());

describe("ProviderSettings", () => {
  it("shows the key form (no Remove) when no key is configured", () => {
    renderWithIntl(<ProviderSettings />);
    expect(screen.getByLabelText("API key")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Save" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Remove" })).not.toBeInTheDocument();
  });

  it("defaults to Anthropic with no base URL field", () => {
    renderWithIntl(<ProviderSettings />);
    expect(screen.getByRole("combobox", { name: "Provider" })).toHaveTextContent("Anthropic");
    expect(screen.queryByLabelText("Base URL")).not.toBeInTheDocument();
  });

  it("loads the live models into a picker", async () => {
    const mutate = vi.fn((_vars, opts) =>
      opts.onSuccess?.({
        status: 200,
        data: {
          models: [
            { id: "claude-opus-4-8", displayName: "Claude Opus 4.8" },
            { id: "claude-haiku-4-5", displayName: "Claude Haiku 4.5" },
          ],
        },
      }),
    );
    useListLlmModelsMock.mockReturnValue({ ...idleModels, mutate });
    renderWithIntl(<ProviderSettings />);

    // The model field starts as a free-text input, then becomes a Select once models load.
    expect((screen.getByLabelText("Model") as HTMLElement).tagName).toBe("INPUT");
    fireEvent.click(screen.getByRole("button", { name: "Load available models" }));
    fireEvent.click(screen.getByRole("combobox", { name: "Model" }));
    expect(await screen.findByRole("option", { name: "Claude Haiku 4.5" })).toBeInTheDocument();
  });

  it("shows the current provider + model and a Remove button when configured", () => {
    useGetLlmConfigMock.mockReturnValue({
      isPending: false,
      data: {
        status: 200,
        data: {
          provider: "anthropic",
          model: "claude-opus-4-8",
          baseUrl: "",
          updatedAt: "2026-07-23T10:00:00Z",
        },
      },
    });
    renderWithIntl(<ProviderSettings />);
    expect(screen.getByText("claude-opus-4-8")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Remove" })).toBeInTheDocument();
    expect(screen.getByLabelText("Replace the API key")).toBeInTheDocument();
  });

  it("submits the key to set the config", () => {
    const mutate = vi.fn();
    useSetLlmConfigMock.mockReturnValue({ ...idleSave, mutate });
    renderWithIntl(<ProviderSettings />);
    fireEvent.change(screen.getByLabelText("API key"), {
      target: { value: "sk-ant-abc" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));
    expect(mutate).toHaveBeenCalledWith(
      {
        orgId: 3,
        data: {
          provider: "anthropic",
          model: "claude-opus-4-8",
          baseUrl: null,
          apiKey: "sk-ant-abc",
        },
      },
      expect.objectContaining({ onSuccess: expect.any(Function) }),
    );
  });

  it("reveals a base URL field and submits the OpenAI-compatible provider", async () => {
    const mutate = vi.fn();
    useSetLlmConfigMock.mockReturnValue({ ...idleSave, mutate });
    renderWithIntl(<ProviderSettings />);

    fireEvent.click(screen.getByRole("combobox", { name: "Provider" }));
    fireEvent.click(await screen.findByRole("option", { name: "OpenAI-compatible" }));
    fireEvent.change(screen.getByLabelText("Base URL"), {
      target: { value: "https://vllm.test/v1" },
    });
    fireEvent.change(screen.getByLabelText("Model"), { target: { value: "llama-3" } });
    fireEvent.change(screen.getByLabelText("API key"), { target: { value: "sk-x" } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    expect(mutate).toHaveBeenCalledWith(
      {
        orgId: 3,
        data: {
          provider: "openai-compat",
          model: "llama-3",
          baseUrl: "https://vllm.test/v1",
          apiKey: "sk-x",
        },
      },
      expect.objectContaining({ onSuccess: expect.any(Function) }),
    );
  });

  it("does not submit an OpenAI-compatible config without a base URL", async () => {
    const mutate = vi.fn();
    useSetLlmConfigMock.mockReturnValue({ ...idleSave, mutate });
    renderWithIntl(<ProviderSettings />);

    fireEvent.click(screen.getByRole("combobox", { name: "Provider" }));
    fireEvent.click(await screen.findByRole("option", { name: "OpenAI-compatible" }));
    fireEvent.change(screen.getByLabelText("API key"), { target: { value: "sk-x" } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    expect(mutate).not.toHaveBeenCalled();
  });

  it("explains the plan gate when the save 409s", () => {
    useSetLlmConfigMock.mockReturnValue({
      ...idleSave,
      isError: true,
      error: new ConduxApiError("PUT", "/llm-config", 409, "byo_key_requires_upgrade"),
    });
    renderWithIntl(<ProviderSettings />);
    expect(screen.getByText(/require the Enterprise plan/)).toBeInTheDocument();
  });

  it("shows a read-only notice for non-admins", () => {
    useCurrentOrgMock.mockReturnValue({
      status: OrgStatus.Ready,
      org: { id: 3, tier: 3 },
      role: "member",
    });
    renderWithIntl(<ProviderSettings />);
    expect(screen.getByText(/Only an organization admin/)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Save" })).not.toBeInTheDocument();
  });
});
