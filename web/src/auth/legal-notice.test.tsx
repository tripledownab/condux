import { screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { renderWithIntl } from "@/src/test-utils/render-with-intl";
import { LegalNotice } from "./legal-notice";

// The notice is what makes clause 1 of the terms true ("creating an account accepts them"), so these
// assert the two ways it would quietly stop being true: no links when a site IS configured, and our
// terms shown on a deployment that has none of ours to accept.
//
// Where it appears in the signup form is asserted in auth-form.test.tsx, which already has the mocks.

afterEach(() => {
  vi.unstubAllEnvs();
});

describe("LegalNotice", () => {
  it("links the terms and the privacy policy on the configured site", () => {
    vi.stubEnv("NEXT_PUBLIC_CONDUX_LEGAL_URL", "https://condux.ai");
    renderWithIntl(<LegalNotice />);

    expect(screen.getByRole("link", { name: "Terms of Service" })).toHaveAttribute(
      "href",
      "https://condux.ai/terms",
    );
    expect(screen.getByRole("link", { name: "Privacy Policy" })).toHaveAttribute(
      "href",
      "https://condux.ai/privacy",
    );
  });

  it("renders nothing when the deployment publishes no terms", () => {
    vi.stubEnv("NEXT_PUBLIC_CONDUX_LEGAL_URL", "");
    const { container } = renderWithIntl(<LegalNotice />);

    expect(container).toBeEmptyDOMElement();
  });
});
