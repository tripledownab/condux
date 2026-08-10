import { describe, expect, it } from "vitest";
import { renderWithIntl } from "@/src/test-utils/render-with-intl";
import { NotificationChannel } from "./format";
import { MarkdownHint } from "./markdown-hint";

describe("MarkdownHint", () => {
  it("shows Slack mrkdwn syntax for a Slack channel", () => {
    const { container } = renderWithIntl(<MarkdownHint channel={NotificationChannel.Slack} />);
    expect(container.textContent).toContain("Slack markdown:");
    // Slack's single-asterisk bold and pipe-link syntax, so the dialect difference is visible.
    expect(container.textContent).toContain("*bold*");
    expect(container.textContent).toContain("<url|label>");
  });

  it("shows Discord markdown syntax for a Discord channel", () => {
    const { container } = renderWithIntl(<MarkdownHint channel={NotificationChannel.Discord} />);
    expect(container.textContent).toContain("Discord markdown:");
    expect(container.textContent).toContain("**bold**");
    expect(container.textContent).toContain("[label](url)");
  });

  it("notes plain text for email, with no syntax example", () => {
    const { container } = renderWithIntl(<MarkdownHint channel={NotificationChannel.Email} />);
    expect(container.textContent).toContain("Sent as plain text (no markdown).");
    expect(container.querySelector("code")).toBeNull();
  });
});
