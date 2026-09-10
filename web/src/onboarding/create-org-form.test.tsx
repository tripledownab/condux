import { fireEvent, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { renderWithIntl } from "@/src/test-utils/render-with-intl";

const useCreateOrgMock = vi.fn();
vi.mock("@tanstack/react-query", () => ({
  useQueryClient: () => ({ invalidateQueries: vi.fn() }),
}));
vi.mock("@/src/api/generated/condux", () => ({
  useCreateOrg: (options: unknown) => useCreateOrgMock(options),
  getListMyOrgsQueryKey: () => ["orgs"],
}));

import { CreateOrgForm } from "./create-org-form";

function renderWithMutate() {
  const mutate = vi.fn();
  useCreateOrgMock.mockReturnValue({ mutate, isPending: false, isError: false });
  renderWithIntl(<CreateOrgForm />);
  return mutate;
}

function submit(name: string) {
  fireEvent.change(screen.getByRole("textbox"), { target: { value: name } });
  fireEvent.submit(screen.getByRole("textbox").closest("form") as HTMLFormElement);
}

afterEach(() => vi.clearAllMocks());

describe("CreateOrgForm", () => {
  // The slug used to be derived here and posted with the name, which put a value the database
  // constrained behind browser code. The server owns it now, so this form sends the name and nothing else.
  it("sends only the trimmed name", () => {
    const mutate = renderWithMutate();

    submit("  Acme Inc  ");

    expect(mutate).toHaveBeenCalledWith({ data: { name: "Acme Inc" } });
  });

  // A name of only non-ASCII characters slugified to "" in the browser, and the empty slug was accepted
  // once and collided for ever after. Nothing about such a name should stop the form submitting it.
  it("submits a name with no ASCII letters unchanged", () => {
    const mutate = renderWithMutate();

    submit("日本語");

    expect(mutate).toHaveBeenCalledWith({ data: { name: "日本語" } });
  });

  it("does not submit a blank name", () => {
    const mutate = renderWithMutate();

    submit("   ");

    expect(mutate).not.toHaveBeenCalled();
  });
});
