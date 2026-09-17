import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { renderWithIntl } from "@/src/test-utils/render-with-intl";

vi.mock("@tanstack/react-query", () => ({
  useQueryClient: () => ({ invalidateQueries: vi.fn() }),
}));

const useVerifyMock = vi.fn();
vi.mock("@/src/api/generated/condux", () => ({
  useVerifySsoDomain: () => useVerifyMock(),
  getGetSsoConfigQueryKey: () => ["sso-config"],
}));

import { SsoDomainVerification } from "./sso-domain-verification";

const mutate = vi.fn();
const config = {
  emailDomain: "acme.test",
  issuer: "https://idp.test/",
  protocol: 0,
  authorizationEndpoint: "https://idp.test/authorize",
  tokenEndpoint: "https://idp.test/token",
  clientId: "client-abc",
  samlSsoUrl: null,
  samlCertificate: null,
  updatedAt: "2026-09-14T12:00:00Z",
  verificationRecordName: "acme.test",
  verificationRecordValue: "condux-domain-verification=abc123",
  verifiedAt: null,
  verificationLostAt: null,
  verificationLapsesAt: null,
};

const verified = { ...config, verifiedAt: "2026-09-15T12:00:00Z" };

beforeEach(() => useVerifyMock.mockReturnValue({ mutate, isPending: false }));
afterEach(() => vi.clearAllMocks());

describe("SsoDomainVerification", () => {
  it("shows the record to publish while the claim is unproved", () => {
    renderWithIntl(<SsoDomainVerification orgId={7} config={config} canManage={true} />);

    expect(screen.getByText("Not verified")).toBeInTheDocument();
    expect(screen.getByText("condux-domain-verification=abc123")).toBeInTheDocument();
    // The consequence has to be on screen, not only in a runbook: a saved config that routes nothing is
    // otherwise indistinguishable from a broken one.
    expect(
      screen.getByText(
        "Add this TXT record to your domain, then verify. Until the domain is verified, sign-in requests for it are not routed to your identity provider.",
      ),
    ).toBeInTheDocument();
  });

  it("verifies on click", async () => {
    renderWithIntl(<SsoDomainVerification orgId={7} config={config} canManage={true} />);

    await userEvent.click(screen.getByRole("button", { name: "Verify" }));

    expect(mutate).toHaveBeenCalledWith({ orgId: 7 }, expect.anything());
  });

  // A check that answered "the record is not there yet" is the ordinary case on a first attempt, and a
  // resolver we could not reach is a fault of ours. Reporting one as the other sends an admin to look at
  // DNS that is already correct.
  it.each([
    ["record_missing", "The record was not found. DNS changes can take a while to publish."],
    ["resolver_unavailable", "Could not reach DNS to check the record. Try again shortly."],
    ["verified", "Domain verified."],
  ])("reports the %s outcome in its own words", (outcome, message) => {
    useVerifyMock.mockReturnValue({
      mutate,
      isPending: false,
      data: { status: 200, data: { outcome, verifiedAt: null } },
    });
    renderWithIntl(<SsoDomainVerification orgId={7} config={config} canManage={true} />);

    expect(screen.getByRole("status")).toHaveTextContent(message);
  });

  it("shows a member the state without offering the action", () => {
    renderWithIntl(<SsoDomainVerification orgId={7} config={verified} canManage={false} />);

    expect(screen.getByText("Verified Sep 15, 2026")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Verify" })).not.toBeInTheDocument();
  });

  // The daily re-check (ADR-0043 slice 2) can take a proof away, so a claim has two more states than it
  // used to. Both are the org's SSO in trouble, and neither may read as "Not verified": an org that did
  // publish the record, and whose record then went missing, is not an org that never started.
  it("shows a claim whose record went missing, with the date it stops routing", () => {
    renderWithIntl(
      <SsoDomainVerification
        orgId={7}
        config={{
          ...verified,
          verificationLostAt: "2026-09-16T12:00:00Z",
          verificationLapsesAt: "2026-09-23T12:00:00Z",
        }}
        canManage={true}
      />,
    );

    expect(screen.getByText("Record missing since Sep 16, 2026")).toBeInTheDocument();
    expect(screen.queryByText("Not verified")).not.toBeInTheDocument();
    // The deadline is the server's, so the panel and the email the org just got agree on the date.
    expect(
      screen.getByText(
        "This TXT record is no longer visible in DNS. Sign-in for this domain keeps working until Sep 23, 2026. Republish the record and the next daily check picks it up, with nothing to click here.",
      ),
    ).toBeInTheDocument();
  });

  it("shows a lapsed claim as lapsed, not as never verified", () => {
    renderWithIntl(
      <SsoDomainVerification
        orgId={7}
        config={{ ...config, verificationLostAt: "2026-09-16T12:00:00Z" }}
        canManage={true}
      />,
    );

    expect(screen.getByText("Verification lapsed")).toBeInTheDocument();
    expect(screen.queryByText("Not verified")).not.toBeInTheDocument();
    expect(screen.getByText(/no longer routed to your identity provider/)).toBeInTheDocument();
    // Still actionable: the record is on screen and Verify is the way back.
    expect(screen.getByRole("button", { name: "Verify" })).toBeInTheDocument();
  });
});
