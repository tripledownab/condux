import { ConduxApiError } from "@/src/api/fetcher";

// Maps a failed auth request to a message KEY under the "auth" namespace (the form translates it),
// keyed first on the control-plane error code then on the HTTP status. Returns null when there is no
// specific mapping, so the caller falls back to a generic per-mode message.
const CODE_KEYS: Record<string, string> = {
  email_taken: "errors.emailTaken",
  invalid_credentials: "errors.invalidCredentials",
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
  already_in_org: "errors.alreadyInOrg",
};

export function redirectErrorKey(code: string | null): string | null {
  if (!code) {
    return null;
  }
  return REDIRECT_ERROR_KEYS[code] ?? "errors.signInFailed";
}
