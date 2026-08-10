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
  const errorId = useId();
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");

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
  }, []);

  // Both hooks are always called (hooks cannot be conditional); the active one is picked by mode.
  const login = useLogin();
  const signup = useSignup();
  const mutation = mode === AuthMode.Login ? login : signup;

  const submit = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    mutation.mutate(
      { data: { email, password } },
      {
        onSuccess: async () => {
          // A fresh session invalidates the cached "me" so the guard re-fetches. An explicit `next`
          // (e.g. an invite accept page) wins; otherwise a signup has no org yet (ADR-0018) so it lands
          // on onboarding to create one, and a login lands on home.
          await queryClient.invalidateQueries({ queryKey: getMeQueryKey() });
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

        {errorMessage ? (
          <p id={errorId} role="alert" className="text-sm text-error">
            {errorMessage}
          </p>
        ) : null}

        <button type="submit" disabled={mutation.isPending} className={PRIMARY_BUTTON_CLASS}>
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
    </div>
  );
}
