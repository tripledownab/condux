import { fireEvent, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { renderWithIntl } from "@/src/test-utils/render-with-intl";
import { TokenMintForm } from "./token-mint";

// The mint form's contract is that the FIRST capability option is the selected default, which is what
// makes the least authority the default without a second prop that could disagree with the list. That
// makes option order load-bearing, so it is pinned here rather than only described in a comment.
const READ = { value: "read", label: "Read only" };
const TRIAGE = { value: "triage", label: "Read and triage" };

function render(props: Partial<Parameters<typeof TokenMintForm>[0]> = {}) {
  return renderWithIntl(
    <TokenMintForm namespace="settings.mcpTokens" creating={false} onCreate={vi.fn()} {...props} />,
  );
}

describe("TokenMintForm", () => {
  it("submits the first option when the picker is untouched", () => {
    const onCreate = vi.fn();
    render({ capabilities: [READ, TRIAGE], onCreate });

    fireEvent.click(screen.getByRole("button", { name: "Create token" }));

    expect(onCreate).toHaveBeenCalledWith("", "read");
  });

  it("takes the default from the order, not from the word read", () => {
    const onCreate = vi.fn();
    // Deliberately reversed. A hardcoded "read" default would pass the test above and fail this one,
    // which is the whole point of having both.
    render({ capabilities: [TRIAGE, READ], onCreate });

    fireEvent.click(screen.getByRole("button", { name: "Create token" }));

    expect(onCreate).toHaveBeenCalledWith("", "triage");
  });

  it("returns to the first option after a mint, so the next one is not pre-armed", async () => {
    const onCreate = vi.fn();
    render({ capabilities: [READ, TRIAGE], onCreate });

    fireEvent.click(screen.getByRole("combobox", { name: "What this token may do" }));
    fireEvent.click(await screen.findByRole("option", { name: "Read and triage" }));
    fireEvent.click(screen.getByRole("button", { name: "Create token" }));
    fireEvent.click(screen.getByRole("button", { name: "Create token" }));

    expect(onCreate).toHaveBeenNthCalledWith(1, "", "triage");
    expect(onCreate).toHaveBeenNthCalledWith(2, "", "read");
  });

  it("renders no picker and passes no capability for a token kind that has none", () => {
    const onCreate = vi.fn();
    render({ onCreate });

    expect(screen.queryByRole("combobox")).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Create token" }));
    expect(onCreate).toHaveBeenCalledWith("", undefined);
  });
});
