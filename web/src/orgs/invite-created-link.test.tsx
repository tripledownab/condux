import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { renderWithIntl } from "@/src/test-utils/render-with-intl";
import { InviteCreatedLink } from "./invite-created-link";

describe("InviteCreatedLink", () => {
  it("shows the accept link for the invited email", () => {
    renderWithIntl(<InviteCreatedLink email="dev@thing.se" token="tok123" />);

    expect(screen.getByText("Invite link for dev@thing.se")).toBeInTheDocument();
    const input = screen.getByLabelText("Invite link for dev@thing.se") as HTMLInputElement;
    expect(input.value).toContain("/invite?token=tok123");
  });

  it("copies the link to the clipboard and confirms", async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.assign(navigator, { clipboard: { writeText } });
    renderWithIntl(<InviteCreatedLink email="dev@thing.se" token="tok123" />);

    await userEvent.click(screen.getByRole("button", { name: "Copy" }));

    expect(writeText).toHaveBeenCalledWith(expect.stringContaining("/invite?token=tok123"));
    expect(await screen.findByRole("button", { name: "Copied" })).toBeInTheDocument();
  });
});
