import { fireEvent, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { renderWithIntl } from "@/src/test-utils/render-with-intl";
import { ConfirmDialog } from "./confirm-dialog";

describe("ConfirmDialog", () => {
  it("fires onConfirm when the confirm button is clicked", () => {
    const onConfirm = vi.fn();
    renderWithIntl(
      <ConfirmDialog
        open
        onOpenChange={vi.fn()}
        title="Remove member"
        body="Are you sure?"
        confirmLabel="Remove"
        cancelLabel="Cancel"
        onConfirm={onConfirm}
      />,
    );

    fireEvent.click(screen.getByRole("button", { name: "Remove" }));
    expect(onConfirm).toHaveBeenCalledOnce();
  });

  it("does not render its content when closed", () => {
    renderWithIntl(
      <ConfirmDialog
        open={false}
        onOpenChange={vi.fn()}
        title="Remove member"
        body="Are you sure?"
        confirmLabel="Remove"
        cancelLabel="Cancel"
        onConfirm={vi.fn()}
      />,
    );

    expect(screen.queryByText("Remove member")).not.toBeInTheDocument();
  });
});
