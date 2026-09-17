import { screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { renderWithIntl } from "@/src/test-utils/render-with-intl";

vi.mock("@tanstack/react-query", () => ({
  useQueryClient: () => ({ invalidateQueries: vi.fn() }),
}));

const useGetMock = vi.fn();
const useSetMock = vi.fn();
const useDeleteMock = vi.fn();
const useMetadataMock = vi.fn();
const useVerifyMock = vi.fn();
vi.mock("@/src/api/generated/condux", () => ({
  useGetSsoConfig: () => useGetMock(),
  useSetSsoConfig: () => useSetMock(),
  useDeleteSsoConfig: () => useDeleteMock(),
  useGetSsoMetadata: () => useMetadataMock(),
  useVerifySsoDomain: () => useVerifyMock(),
  getGetSsoConfigQueryKey: () => ["sso-config"],
}));

const useCurrentOrgMock = vi.fn();
vi.mock("@/src/orgs/current-org", () => ({
  OrgStatus: { Loading: "loading", Error: "error", NoOrg: "no-org", Ready: "ready" },
  useCurrentOrg: () => useCurrentOrgMock(),
}));

import { SsoSettings } from "./sso-settings";

const idle = { mutate: vi.fn(), isPending: false };
const metadata = {
  status: 200,
  data: {
    redirectUri: "https://app.test/api/auth/sso/callback",
    samlEntityId: "https://app.test/api/auth/sso/saml",
    samlAcsUrl: "https://app.test/api/auth/sso/saml/acs",
  },
};

beforeEach(() => {
  useCurrentOrgMock.mockReturnValue({ status: "ready", org: { id: 7 }, role: "owner" });
  // 404 is "no config yet", which is the state the form is offered in.
  useGetMock.mockReturnValue({ data: { status: 404 }, isPending: false });
  useMetadataMock.mockReturnValue({ data: metadata, isPending: false });
  useSetMock.mockReturnValue(idle);
  useDeleteMock.mockReturnValue(idle);
  useVerifyMock.mockReturnValue(idle);
});

afterEach(() => vi.clearAllMocks());

describe("SsoSettings", () => {
  it("offers the form to an owner", () => {
    renderWithIntl(<SsoSettings />);
    expect(screen.getByRole("button", { name: "Save" })).toBeInTheDocument();
    expect(
      screen.queryByText("Only the organization owner can manage SSO."),
    ).not.toBeInTheDocument();
  });

  // The role gate that matters. An admin can write every other org-level integration secret, so this
  // is the one tab where admin is refused: whoever writes the config names the identity provider that
  // can sign any member in, an owner included, with no password and no second factor. The server
  // answers 403 either way; this only decides whether a form is offered that would be refused.
  it("refuses an admin, who is not an owner", () => {
    useCurrentOrgMock.mockReturnValue({ status: "ready", org: { id: 7 }, role: "admin" });
    renderWithIntl(<SsoSettings />);

    expect(screen.getByText("Only the organization owner can manage SSO.")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Save" })).not.toBeInTheDocument();
  });

  // Reading is the half that did NOT move, so a member still sees the stored config. The mock has to
  // return one for that to mean anything: with the 404 the other cases use there is nothing on screen
  // to be visible, and asserting the metadata URL instead would pass without the config read existing
  // at all, since /api/auth/sso/metadata is deployment-wide and not org-scoped.
  it("refuses a plain member, who can still read the stored config", () => {
    useCurrentOrgMock.mockReturnValue({ status: "ready", org: { id: 7 }, role: "member" });
    useGetMock.mockReturnValue({
      data: {
        status: 200,
        data: {
          emailDomain: "acme.test",
          issuer: "https://idp.test/",
          protocol: 0,
          updatedAt: "2026-09-14T12:00:00Z",
          verificationRecordName: "acme.test",
          verificationRecordValue: "condux-domain-verification=abc123",
          verifiedAt: "2026-09-15T12:00:00Z",
          verificationLostAt: null,
          verificationLapsesAt: null,
        },
      },
      isPending: false,
    });
    renderWithIntl(<SsoSettings />);

    expect(screen.getByText("Only the organization owner can manage SSO.")).toBeInTheDocument();
    // The issuer rather than the domain: the domain now appears twice, since the verification record is
    // named after it, and an assertion that matches either would not say which one it found.
    expect(screen.getByText("https://idp.test/")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Save" })).not.toBeInTheDocument();
    // The domain panel shows a member the state and the record, and offers them no Verify button: the
    // server gates it at owner, and a button that always answers 403 is worse than no button.
    expect(screen.queryByRole("button", { name: "Verify" })).not.toBeInTheDocument();
  });
});
