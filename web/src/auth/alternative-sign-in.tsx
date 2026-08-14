"use client";

import { RiBuildingLine, RiGoogleFill } from "@remixicon/react";
import { useTranslations } from "next-intl";
import { apiUrl } from "@/src/api/fetcher";
import { useAuthProviders } from "./use-auth-providers";

const BUTTON_CLASS =
  "flex items-center justify-center gap-2 rounded-md border border-border px-4 py-2 text-sm font-medium text-foreground transition-colors hover:bg-card";

// Static id: the hint is referenced by aria-describedby and there is only ever one SSO button on the
// page, so useId would add a hook for no benefit.
const SSO_HINT_ID = "sso-needs-email";

// The external sign-in options under the email/password form: "Continue with Google" (consumer OIDC, #71)
// and enterprise "Sign in with SSO" (per-org OIDC, #72). Both are full-page navigations to the
// control-plane's start routes (not XHR), so the session cookie the callback sets is first-party. Renders a
// single divider and only the providers the deployment configured, so a stack with neither shows nothing.
// SSO is email-first (it routes by the email's domain), so its button carries the email the user typed and
// stays disabled until one is entered.
export function AlternativeSignIn({ email }: { email: string }) {
  const translate = useTranslations("auth");
  const { google, sso } = useAuthProviders();

  if (!google && !sso) {
    return null;
  }

  const hasEmail = email.trim() !== "";

  return (
    <div className="mt-4">
      <div className="mb-4 flex items-center gap-3 text-xs text-muted-foreground">
        <span className="h-px flex-1 bg-border" />
        {translate("or")}
        <span className="h-px flex-1 bg-border" />
      </div>
      <div className="flex flex-col gap-2">
        {google ? (
          <a href={apiUrl("/api/auth/oauth/google/start")} className={BUTTON_CLASS}>
            <RiGoogleFill className="size-4" />
            {translate("continueWithGoogle")}
          </a>
        ) : null}
        {sso ? (
          // A button, not a link: SSO routes by the email's domain, so it stays disabled (inert) until one
          // is typed, then navigates to the start route with that email (a full-page nav, like the link above).
          <>
            <button
              type="button"
              disabled={!hasEmail}
              aria-describedby={hasEmail ? undefined : SSO_HINT_ID}
              onClick={() =>
                window.location.assign(
                  apiUrl(`/api/auth/sso/start?email=${encodeURIComponent(email)}`),
                )
              }
              className={`${BUTTON_CLASS} disabled:opacity-50`}
            >
              <RiBuildingLine className="size-4" />
              {translate("continueWithSso")}
            </button>
            {/* Stated inline rather than as a title tooltip. A native tooltip needs a hover that touch
                never sends, and a disabled button suppresses pointer events anyway, so the reason the
                control was inert reached nobody — it read as broken even to us. */}
            {hasEmail ? null : (
              <p id={SSO_HINT_ID} className="text-center text-xs text-muted-foreground">
                {translate("ssoNeedsEmail")}
              </p>
            )}
          </>
        ) : null}
      </div>
    </div>
  );
}
