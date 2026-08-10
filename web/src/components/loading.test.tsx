import { screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { renderWithIntl } from "@/src/test-utils/render-with-intl";
import { FullScreenLoading } from "./loading";

describe("FullScreenLoading", () => {
  it("keeps an accessible loading status behind the animated mark", () => {
    renderWithIntl(<FullScreenLoading />);
    // The wordmark is decorative (aria-hidden); the status text is what assistive tech announces.
    expect(screen.getByRole("status")).toHaveTextContent("Loading");
  });
});
