"use client";

import { useQueryClient } from "@tanstack/react-query";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { type FormEvent, useEffect, useId, useState } from "react";
import { getMeQueryKey, useLogin, useSignup } from "@/src/api/generated/condux";
import { FIELD_CLASS, PRIMARY_BUTTON_CLASS } from "@/src/components/form";
import { safeInternalPath } from "@/src/lib/safe-path";
import { ROUTES } from "@/src/routes";
import { AlternativeSignIn } from "./alternative-sign-in";
import { authErrorKey, redirectErrorKey } from "./auth-error";
import { AuthMode } from "./auth-mode";
import { LegalNotice } from "./legal-notice";
import { MfaChallenge } from "./mfa-challenge";

// Non-text, per-mode config. All copy comes from the "auth" catalog namespace keyed by mode
// (auth.login.*, auth.signup.*), so signup and login share this one declarative form.
type AuthConfig = { altHref: string; passwordAutoComplete: "current-password" | "new-password" };

const CONFIG: Record<AuthMode, AuthConfig> = {
  [AuthMode.Login]: { altHref: ROUTES.signup, passwordAutoComplete: "current-password" },
  [AuthMode.Signup]: { altHref: ROUTES.login, passwordAutoComplete: "new-password" },
};

export function AuthForm({ mode }: { mode: AuthMode }) {
  const translate = useTranslations("auth");
  const config = CONFIG[mode];
  const router = useRouter();
  const queryClient = useQueryClient();
  const emailId = useId();
  const passwordId = useId();
  const confirmId = useId();
  const errorId = useId();
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  // Signup only. A typed-once password that was mistyped locks the account permanently: there is no
  // change-password for someone who cannot sign in, and the email cannot be reused because signup
  // answers 409. Confirming is what stops the unrecoverable case from being created at all.
  const [confirm, setConfirm] = useState("");
  const confirming = mode === AuthMode.Signup;
  const mismatch = confirming && confirm !== "" && confirm !== password;
  // Set when login reports the account has a second factor. The session cookie is already set at that
  // point, half-authenticated, so the challenge needs nothing else carried across.
  const [mfaRequired, setMfaRequired] = useState(false);

  // A failed Google/SSO callback redirects back to /login?error=<code>; surface it once on mount.
  // Also read an optional `next` (an app-internal path to land on after auth, e.g. an invite accept
  // page). Read from window (not useSearchParams) so the auth pages don't need a Suspense boundary to
  // prerender.
  const [redirectError, setRedirectError] = useState<string | null>(null);
  const [next, setNext] = useState<string | null>(null);
  useEffect(() => {
    const params = new URLSearchParams(window.location.search);
    setRedirectError(redirectErrorKey(params.get("error")));
    setNext(safeInternalPath(params.get("next")));
    // A redirect sign-in (Google) cannot hand back JSON, so it says "challenge needed" in the URL. The
    // cookie already holds the real state, so this only decides which form to draw: setting it by hand
    // gets an attacker a code box and a session that still authenticates nothing.
    if (params.get("mfa") === "1") {
      setMfaRequired(true);
    }
  }, []);

  // Both hooks are always called (hooks cannot be conditional); the active one is picked by mode.
  const login = useLogin();
  const signup = useSignup();
  const mutation = mode === AuthMode.Login ? login : signup;

  const submit = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    // Guarded here as well as by the input's own validity, because the form is noValidate.
    if (confirming && confirm !== password) {
      return;
    }
    mutation.mutate(
      { data: { email, password } },
      {
        onSuccess: (result) => {
          // A second factor means the session just issued authenticates nothing yet, so do NOT navigate:
          // the guard would bounce straight back. Swap in the challenge instead.
          // Optional chaining because this is only a UX hint. If it were ever absent the client would
          // navigate, the still-pending session would fail /me, and AuthGuard would send the user back
          // to sign in. The gate is the server refusing to resolve the session, never this branch.
          if (result?.status === 200 && result.data.mfaRequired) {
            setMfaRequired(true);
            return;
          }

          // A fresh session invalidates the cached "me" so the guard re-fetches. An explicit `next`
          // (e.g. an invite accept page) wins; otherwise a signup has no org yet (ADR-0018) so it lands
          // on onboarding to create one, and a login lands on home.
          // Remove, not invalidate. Invalidating marks the cached identity stale but leaves it readable, and an
          // INACTIVE query is not refetched until something mounts it. AuthGuard then mounts, reads the
          // stale value synchronously (a cached 401 keeps isError true while the refetch is in flight) and
          // redirects to /login before the fresh answer lands. Removing it means the guard sees no data at
          // all, shows its spinner, and decides on the real response.
          queryClient.removeQueries({ queryKey: getMeQueryKey() });
          router.replace(next ?? (mode === AuthMode.Signup ? ROUTES.onboarding : ROUTES.home));
        },
      },
    );
  };

  // A specific error key when the API gave one, else the generic per-mode message; a failed Google/SSO
  // sign-in takes precedence since it is the most recent action the user took.
  const errorMessage = redirectError
    ? translate(redirectError)
    : mutation.isError
      ? translate(authErrorKey(mutation.error) ?? `${mode}.error`)
      : null;
  const describedBy = errorMessage ? errorId : undefined;

  if (mfaRequired) {
    return (
      <div className="rounded-lg border border-border bg-card p-6">
        <MfaChallenge onVerified={() => router.replace(next ?? ROUTES.home)} />
      </div>
    );
  }

  return (
    <div className="rounded-lg border border-border bg-card p-6">
      <h1 className="font-heading text-xl font-semibold text-foreground">
        {translate(`${mode}.title`)}
      </h1>

      <form onSubmit={submit} className="mt-5 flex flex-col gap-4" noValidate>
        <div className="flex flex-col gap-1.5">
          <label htmlFor={emailId} className="text-sm text-muted-foreground">
            {translate("email")}
          </label>
          <input
            id={emailId}
            type="email"
            required
            autoComplete="email"
            aria-describedby={describedBy}
            aria-invalid={Boolean(errorMessage)}
            value={email}
            onChange={(event) => setEmail(event.target.value)}
            className={FIELD_CLASS}
          />
        </div>

        <div className="flex flex-col gap-1.5">
          <label htmlFor={passwordId} className="text-sm text-muted-foreground">
            {translate("password")}
          </label>
          <input
            id={passwordId}
            type="password"
            required
            minLength={8}
            autoComplete={config.passwordAutoComplete}
            aria-describedby={describedBy}
            aria-invalid={Boolean(errorMessage)}
            value={password}
            onChange={(event) => setPassword(event.target.value)}
            className={FIELD_CLASS}
          />
        </div>

        {confirming ? (
          <div className="flex flex-col gap-1.5">
            <label htmlFor={confirmId} className="text-sm text-muted-foreground">
              {translate("confirmPassword")}
            </label>
            <input
              id={confirmId}
              type="password"
              required
              minLength={8}
              autoComplete="new-password"
              aria-invalid={mismatch}
              value={confirm}
              onChange={(event) => setConfirm(event.target.value)}
              className={FIELD_CLASS}
            />
            {mismatch ? (
              <p role="alert" className="text-sm text-error">
                {translate("passwordMismatch")}
              </p>
            ) : null}
          </div>
        ) : null}

        {/* Only on login: someone signing up has no password to have forgotten. */}
        {mode === AuthMode.Login ? (
          <Link href={ROUTES.forgotPassword} className="text-sm underline underline-offset-4">
            {translate("forgotLink")}
          </Link>
        ) : null}

        {errorMessage ? (
          <p id={errorId} role="alert" className="text-sm text-error">
            {errorMessage}
          </p>
        ) : null}

        <button
          type="submit"
          disabled={mutation.isPending || (confirming && confirm !== password)}
          className={PRIMARY_BUTTON_CLASS}
        >
          {mutation.isPending ? translate(`${mode}.pending`) : translate(`${mode}.submit`)}
        </button>
      </form>

      <AlternativeSignIn email={email} />

      <p className="mt-4 text-center text-sm text-muted-foreground">
        {translate(`${mode}.altPrompt`)}{" "}
        <Link
          href={next ? `${config.altHref}?next=${encodeURIComponent(next)}` : config.altHref}
          className="text-primary hover:underline"
        >
          {translate(`${mode}.altLabel`)}
        </Link>
      </p>

      {mode === AuthMode.Signup ? <LegalNotice /> : null}
    </div>
  );
}
