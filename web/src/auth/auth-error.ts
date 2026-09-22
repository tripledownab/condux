import { ConduxApiError } from "@/src/api/fetcher";

// Maps a failed auth request to a message KEY under the "auth" namespace (the form translates it),
// keyed first on the control-plane error code then on the HTTP status. Returns null when there is no
// specific mapping, so the caller falls back to a generic per-mode message.
// Exported so the test can check every value against the real message catalogue rather than a
// hand-copied list of them. A code the control-plane returns and this map does not know falls back to
// the generic per-mode message, which for a refusal that will never succeed reads as "try again".
export const CODE_KEYS: Record<string, string> = {
  email_taken: "errors.emailTaken",
  invalid_credentials: "errors.invalidCredentials",
  platform_admin_reserved: "errors.platformAdminReserved",
};

const STATUS_KEYS: Record<number, string> = {
  401: "errors.wrongCredentials",
};

export function authErrorKey(error: unknown): string | null {
  if (error instanceof ConduxApiError) {
    if (error.code && CODE_KEYS[error.code]) {
      return CODE_KEYS[error.code];
    }
    if (STATUS_KEYS[error.status]) {
      return STATUS_KEYS[error.status];
    }
  }
  return null;
}

// The OAuth/SSO callbacks (redirect flows, not XHR) bounce failures back to /login?error=<code>. Map a
// known code to a message KEY under "auth"; an unknown code falls back to the generic sign-in failure.
const REDIRECT_ERROR_KEYS: Record<string, string> = {
  oauth_failed: "errors.oauthFailed",
  sso_failed: "errors.ssoFailed",
  sso_not_available: "errors.ssoNotAvailable",
  sso_domain_mismatch: "errors.ssoDomainMismatch",
  sso_invite_required: "errors.ssoInviteRequired",
};

export function redirectErrorKey(code: string | null): string | null {
  if (!code) {
    return null;
  }
  return REDIRECT_ERROR_KEYS[code] ?? "errors.signInFailed";
}
