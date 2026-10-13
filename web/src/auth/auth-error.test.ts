import { describe, expect, it } from "vitest";
import messages from "@/messages/en.json";
import { ConduxApiError } from "@/src/api/fetcher";
import { authErrorKey, CODE_KEYS, redirectErrorKey } from "./auth-error";

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

  // The redirect map below has had this check for a while and this one did not, which is how a code
  // the control-plane already returned reached the form with no message of its own. Iterates the map
  // rather than a list of its keys, so adding a code cannot skip the check.
  it("maps every code to a message that exists", () => {
    for (const [code, key] of Object.entries(CODE_KEYS)) {
      expect(Object.keys(messages.auth.errors), code).toContain(key.replace(/^errors\./, ""));
    }
  });

  // A refusal that can never succeed must not render as the generic "try again": that is the whole
  // reason this code is distinct from email_taken rather than folded into it.
  it("gives the reserved platform-admin address its own message", () => {
    const error = new ConduxApiError("POST", "/api/auth/signup", 409, "platform_admin_reserved");
    expect(authErrorKey(error)).toBe("errors.platformAdminReserved");
  });
});

describe("redirectErrorKey", () => {
  it("maps each SSO refusal to its own message, not the generic one", () => {
    expect(redirectErrorKey("sso_invite_required")).toBe("errors.ssoInviteRequired");
    expect(redirectErrorKey("sso_platform_admin")).toBe("errors.ssoPlatformAdmin");
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
      "sso_platform_admin",
      "anything_unmapped",
    ];
    for (const code of codes) {
      const key = redirectErrorKey(code)?.replace(/^errors\./, "");
      expect(key, code).toBeDefined();
      expect(Object.keys(messages.auth.errors), code).toContain(key);
    }
  });
});

describe("redirectErrorKey on a code nobody issued", () => {
  it("falls back for a code that names an inherited property", () => {
    // The code is a query value anyone can type into a URL; it must never resolve to a function.
    expect(redirectErrorKey("constructor")).toBe("errors.signInFailed");
    expect(redirectErrorKey("toString")).toBe("errors.signInFailed");
  });
});
