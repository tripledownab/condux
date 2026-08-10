import { describe, expect, it } from "vitest";
import { ConduxApiError } from "@/src/api/fetcher";
import { authErrorKey } from "./auth-error";

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
