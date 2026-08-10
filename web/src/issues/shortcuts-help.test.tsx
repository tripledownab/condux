import { fireEvent, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { renderWithIntl } from "@/src/test-utils/render-with-intl";
import { ShortcutsHelp } from "./shortcuts-help";

describe("ShortcutsHelp", () => {
  it("opens on ? and lists the shortcuts from the registry", () => {
    renderWithIntl(<ShortcutsHelp />);
    expect(screen.queryByText("Keyboard shortcuts")).not.toBeInTheDocument();

    fireEvent.keyDown(document.body, { key: "?" });
    expect(screen.getByText("Keyboard shortcuts")).toBeInTheDocument();
    expect(screen.getByText("Next issue")).toBeInTheDocument();
    expect(screen.getByText("Resolve / unresolve")).toBeInTheDocument();

    fireEvent.keyDown(document.body, { key: "?" });
    expect(screen.queryByText("Keyboard shortcuts")).not.toBeInTheDocument();
  });
});
