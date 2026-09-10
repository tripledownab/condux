"use client";

import Link from "next/link";
import { useTranslations } from "next-intl";
import { type FormEvent, useEffect, useId, useState } from "react";
import { useResetPassword } from "@/src/api/generated/condux";
import { FIELD_CLASS, PRIMARY_BUTTON_CLASS } from "@/src/components/form";
import { Notice } from "@/src/components/notice";
import { ROUTES } from "@/src/routes";

/**
 * Setting a new password from an emailed link. Public, and deliberately does NOT sign the user in on
 * success: it sends them to sign in, so an account with a second factor is still challenged for it.
 * Signing them in here would make reaching the mailbox enough to bypass MFA entirely.
 *
 * The confirmation field matters more here than anywhere: someone using this page has already lost one
 * password, and a mistyped replacement would send them straight back to it.
 */
export function ResetPasswordForm() {
  const translate = useTranslations("auth.reset");
  const passwordId = useId();
  const confirmId = useId();
  const [token, setToken] = useState<string | null>(null);
  const [password, setPassword] = useState("");
  const [confirm, setConfirm] = useState("");
  const reset = useResetPassword();

  // Read from window rather than useSearchParams, so this page needs no Suspense boundary (same reason
  // as the other auth pages).
  useEffect(() => {
    setToken(new URLSearchParams(window.location.search).get("token"));
  }, []);

  const mismatch = confirm !== "" && confirm !== password;
  const canSubmit =
    Boolean(token) && password.length >= 8 && confirm === password && !reset.isPending;

  const submit = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    if (!canSubmit || token === null) {
      return;
    }
    reset.mutate({ data: { token, newPassword: password } });
  };

  if (reset.isSuccess) {
    return (
      <div className="rounded-lg border border-border bg-card p-6">
        <h1 className="font-heading text-xl font-semibold text-foreground">{translate("title")}</h1>
        <p className="mt-3 text-sm text-muted-foreground">{translate("done")}</p>
        <Link
          href={ROUTES.login}
          className="mt-5 inline-block text-sm underline underline-offset-4"
        >
          {translate("signIn")}
        </Link>
      </div>
    );
  }

  return (
    <div className="rounded-lg border border-border bg-card p-6">
      <h1 className="font-heading text-xl font-semibold text-foreground">{translate("title")}</h1>

      {token === "" || token === null ? (
        <div className="mt-3">
          <Notice>{translate("noToken")}</Notice>
          <Link
            href={ROUTES.forgotPassword}
            className="mt-4 inline-block text-sm underline underline-offset-4"
          >
            {translate("askAgain")}
          </Link>
        </div>
      ) : (
        <form onSubmit={submit} className="mt-5 flex flex-col gap-4" noValidate>
          {/* Stated up front because the page cannot tell a spent link from a live one. Checking would
              need an endpoint that answers "is this token valid", which is a way to test tokens without
              spending them, so the honest trade is to warn rather than to probe. */}
          <p className="text-sm text-muted-foreground">{translate("oneUse")}</p>

          <div className="flex flex-col gap-1.5">
            <label htmlFor={passwordId} className="text-sm text-muted-foreground">
              {translate("password")}
            </label>
            <input
              id={passwordId}
              type="password"
              required
              minLength={8}
              autoComplete="new-password"
              value={password}
              onChange={(event) => setPassword(event.target.value)}
              className={FIELD_CLASS}
            />
          </div>

          <div className="flex flex-col gap-1.5">
            <label htmlFor={confirmId} className="text-sm text-muted-foreground">
              {translate("confirm")}
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
                {translate("mismatch")}
              </p>
            ) : null}
          </div>

          <button type="submit" disabled={!canSubmit} className={PRIMARY_BUTTON_CLASS}>
            {reset.isPending ? translate("saving") : translate("submit")}
          </button>

          {/* A link that is expired, already used or simply wrong all answer the same, because telling
              them apart would tell someone guessing which guesses were closer. */}
          {reset.isError ? (
            <p role="alert" className="text-sm text-error">
              {translate("invalidToken")}{" "}
              <Link href={ROUTES.forgotPassword} className="underline underline-offset-4">
                {translate("askAgain")}
              </Link>
            </p>
          ) : null}
        </form>
      )}
    </div>
  );
}
