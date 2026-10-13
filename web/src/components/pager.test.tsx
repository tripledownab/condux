import { fireEvent, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { renderWithIntl } from "@/src/test-utils/render-with-intl";
import { Pager } from "./pager";

describe("Pager", () => {
  it("renders nothing for a single page", () => {
    renderWithIntl(<Pager page={0} pageCount={1} onPageChange={vi.fn()} />);
    expect(screen.queryByRole("navigation")).not.toBeInTheDocument();
  });

  it("numbers the pages one-based and marks the current one", () => {
    renderWithIntl(<Pager page={1} pageCount={3} onPageChange={vi.fn()} />);

    expect(screen.getByRole("button", { name: "Page 2" })).toHaveAttribute("aria-current", "page");
    expect(screen.getByRole("button", { name: "Page 1" })).not.toHaveAttribute("aria-current");
    expect(screen.getByRole("button", { name: "Page 3" })).toHaveTextContent("3");
  });

  it("jumps to a numbered page", () => {
    const onPageChange = vi.fn();
    renderWithIntl(<Pager page={0} pageCount={3} onPageChange={onPageChange} />);

    fireEvent.click(screen.getByRole("button", { name: "Page 3" }));
    expect(onPageChange).toHaveBeenCalledWith(2);
  });

  it("steps with previous and next, disabled at the ends", () => {
    const onPageChange = vi.fn();
    const { rerender } = renderWithIntl(
      <Pager page={0} pageCount={3} onPageChange={onPageChange} />,
    );
    expect(screen.getByRole("button", { name: "Previous page" })).toBeDisabled();
    fireEvent.click(screen.getByRole("button", { name: "Next page" }));
    expect(onPageChange).toHaveBeenCalledWith(1);

    rerender(<Pager page={2} pageCount={3} onPageChange={onPageChange} />);
    expect(screen.getByRole("button", { name: "Next page" })).toBeDisabled();
    fireEvent.click(screen.getByRole("button", { name: "Previous page" }));
    expect(onPageChange).toHaveBeenCalledWith(1);
  });

  it("shows gaps for skipped runs on a long list", () => {
    renderWithIntl(<Pager page={10} pageCount={20} onPageChange={vi.fn()} />);

    expect(screen.getAllByText("…")).toHaveLength(2);
    expect(screen.getByRole("button", { name: "Page 20" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Page 5" })).not.toBeInTheDocument();
  });
});
