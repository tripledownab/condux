import { describe, expect, it } from "vitest";
import messages from "@/messages/en.json";
import { ConduxApiError } from "@/src/api/fetcher";
import { authErrorKey, redirectErrorKey } from "./auth-error";

describe("authErrorKey", () => {
  it("maps a known error code to its message key", () => {
    const error = new ConduxApiError("POST", "/api/auth/signup", 409, "email_taken");
    expect(authErrorKey(error)).toBe("errors.emailTaken");
  });

  it("maps a bare 401 (no code) to the wrong-credentials key", () => {
    const error = new ConduxApiError("POST", "/api/auth/login", 401);
    expect(authErrorKey(error)).toBe("errors.wrongCredentials");
  });

  it("returns null for a non-API error", () => {
    expect(authErrorKey(new Error("network down"))).toBeNull();
  });

  it("returns null for an unmapped status", () => {
    expect(authErrorKey(new ConduxApiError("GET", "/api/auth/me", 500))).toBeNull();
  });
});

describe("redirectErrorKey", () => {
  it("maps the SSO membership refusal to its own message, not the generic one", () => {
    expect(redirectErrorKey("sso_invite_required")).toBe("errors.ssoInviteRequired");
  });

  it("falls back to the generic failure for an unknown code", () => {
    expect(redirectErrorKey("something_new")).toBe("errors.signInFailed");
  });

  it("returns null when there is no error in the URL", () => {
    expect(redirectErrorKey(null)).toBeNull();
  });

  // The map returns a KEY, and the form translates it under the "auth" namespace. A key with no
  // message renders as the key itself, which looks like a bug rather than a refusal, so every mapped
  // code is checked against the real catalogue instead of against a copy of the same list.
  it("maps every code to a message that exists", () => {
    const codes = [
      "oauth_failed",
      "sso_failed",
      "sso_not_available",
      "sso_domain_mismatch",
      "sso_invite_required",
      "anything_unmapped",
    ];
    for (const code of codes) {
      const key = redirectErrorKey(code)?.replace(/^errors\./, "");
      expect(key, code).toBeDefined();
      expect(Object.keys(messages.auth.errors), code).toContain(key);
    }
  });
});
