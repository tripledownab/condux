"use client";

import { useQueryClient } from "@tanstack/react-query";
import { useTranslations } from "next-intl";
import { type FormEvent, useId, useState } from "react";
import { ConduxApiError } from "@/src/api/fetcher";
import { getMeQueryKey, useVerifyMfa } from "@/src/api/generated/condux";
import { FIELD_CLASS, PRIMARY_BUTTON_CLASS } from "@/src/components/form";

/**
 * The second-factor step, shown in place of the password form once the password has been accepted.
 *
 * In place rather than on its own route because the session cookie already carries the state: login
 * returns a half-authenticated session, so there is nothing to thread through a URL and nothing to lose
 * if the tab is closed. A refresh drops back to the password form, which is the correct outcome.
 */
export function MfaChallenge({ onVerified }: { onVerified: () => void }) {
  const translate = useTranslations("auth.mfa");
  const queryClient = useQueryClient();
  const codeId = useId();
  const errorId = useId();
  const [code, setCode] = useState("");
  const verify = useVerifyMfa();

  const submit = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    verify.mutate(
      { data: { code } },
      {
        onSuccess: () => {
          // Remove, not invalidate. Invalidating marks the cached identity stale but leaves it readable, and an
          // INACTIVE query is not refetched until something mounts it. AuthGuard then mounts, reads the
          // stale value synchronously (a cached 401 keeps isError true while the refetch is in flight) and
          // redirects to /login before the fresh answer lands. Removing it means the guard sees no data at
          // all, shows its spinner, and decides on the real response.
          queryClient.removeQueries({ queryKey: getMeQueryKey() });
          onVerified();
        },
      },
    );
  };

  // "Too many attempts" is worth distinguishing because it tells the user to start over rather than
  // keep typing. Everything else is one message on purpose: separating "wrong code" from "already used"
  // would confirm to an attacker that a value they tried was real.
  const errorMessage = verify.isError
    ? verify.error instanceof ConduxApiError && verify.error.code === "too_many_attempts"
      ? translate("errors.tooManyAttempts")
      : translate("errors.invalidCode")
    : null;

  return (
    <form className="space-y-4" onSubmit={submit} noValidate>
      <div>
        <h1 className="font-heading text-lg">{translate("challengeTitle")}</h1>
        <p className="mt-1 text-sm text-muted-foreground">{translate("challengeHint")}</p>
      </div>

      <div>
        <label className="block text-sm" htmlFor={codeId}>
          {translate("codeLabel")}
        </label>
        <input
          id={codeId}
          className={FIELD_CLASS}
          value={code}
          onChange={(event) => setCode(event.target.value)}
          // Deliberately text, not number: a numeric input strips the leading zero a TOTP code can
          // start with, and a recovery code is not numeric at all. One field takes both, because the
          // user should not have to know which kind of secret they are holding.
          type="text"
          inputMode="text"
          autoComplete="one-time-code"
          // The password form has just been replaced by this one, so without moving focus a keyboard or
          // screen-reader user is left with focus on an element that no longer exists and has to hunt for
          // the single field on the page.
          // biome-ignore lint/a11y/noAutofocus: the step exists only to take this one value
          autoFocus
          required
          aria-describedby={errorMessage ? errorId : undefined}
        />
      </div>

      {errorMessage ? (
        <p id={errorId} role="alert" className="text-sm text-destructive">
          {errorMessage}
        </p>
      ) : null}

      <button className={PRIMARY_BUTTON_CLASS} type="submit" disabled={verify.isPending}>
        {verify.isPending ? translate("verifying") : translate("verify")}
      </button>

      <p className="text-xs text-muted-foreground">{translate("recoveryHint")}</p>
    </form>
  );
}
