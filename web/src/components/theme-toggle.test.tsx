import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ThemeProvider } from "next-themes";
import { describe, expect, it } from "vitest";
import { renderWithIntl } from "@/src/test-utils/render-with-intl";
import { ThemeToggle } from "./theme-toggle";

function renderToggle() {
  return renderWithIntl(
    <ThemeProvider attribute="data-theme" defaultTheme="dark" enableSystem>
      <ThemeToggle />
    </ThemeProvider>,
  );
}

describe("ThemeToggle", () => {
  it("renders a control for every theme option", () => {
    renderToggle();
    for (const label of ["Light", "Dark", "System"]) {
      expect(screen.getByRole("button", { name: label })).toBeInTheDocument();
    }
  });

  it("marks the chosen theme as pressed after selection", async () => {
    renderToggle();
    const light = screen.getByRole("button", { name: "Light" });
    await userEvent.click(light);
    expect(light).toHaveAttribute("aria-pressed", "true");
    expect(screen.getByRole("button", { name: "Dark" })).toHaveAttribute("aria-pressed", "false");
  });
});
