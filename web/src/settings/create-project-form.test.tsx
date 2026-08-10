import { fireEvent, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { renderWithIntl } from "@/src/test-utils/render-with-intl";

vi.mock("@tanstack/react-query", () => ({
  useQueryClient: () => ({ invalidateQueries: vi.fn() }),
}));

const useCreateProjectMock = vi.fn();
vi.mock("@/src/api/generated/condux", () => ({
  useCreateProject: () => useCreateProjectMock(),
  getListProjectsQueryKey: () => ["projects"],
}));

import { CreateProjectForm } from "./create-project-form";

const idle = { mutate: vi.fn(), isPending: false, isError: false };

afterEach(() => vi.clearAllMocks());

describe("CreateProjectForm", () => {
  it("picks the platform from a searchable combobox and submits the value", async () => {
    const mutate = vi.fn();
    useCreateProjectMock.mockReturnValue({ ...idle, mutate });
    renderWithIntl(<CreateProjectForm orgId={1} />);

    // The platform control is a combobox (not a text box), defaulting to JavaScript / Node.
    const platform = screen.getByRole("combobox", { name: "Platform" });
    expect(platform).toHaveTextContent("JavaScript / Node");

    fireEvent.change(screen.getByLabelText("Project name"), { target: { value: "api" } });
    fireEvent.click(platform);
    fireEvent.click(await screen.findByRole("option", { name: /Python/ }));
    fireEvent.click(screen.getByRole("button", { name: "Create project" }));

    expect(mutate).toHaveBeenCalledWith(
      { orgId: 1, data: { name: "api", platform: "python" } },
      expect.objectContaining({ onSuccess: expect.any(Function) }),
    );
  });
});
