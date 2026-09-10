"use client";

import Link from "next/link";
import { useTranslations } from "next-intl";
import { type FormEvent, useId, useState } from "react";
import { useForgotPassword } from "@/src/api/generated/condux";
import { FIELD_CLASS, PRIMARY_BUTTON_CLASS } from "@/src/components/form";
import { ROUTES } from "@/src/routes";

/**
 * Asking for a reset link. Public, because the people who need it are the ones who cannot sign in.
 *
 * The confirmation never says whether the address had an account, and neither does the API. Anything
 * else here would turn the page into a way to test which addresses are customers, which is worth more
 * to someone probing than the small convenience of a clearer message is to a user who mistyped.
 */
export function ForgotPasswordForm() {
  const translate = useTranslations("auth.forgot");
  const emailId = useId();
  const [email, setEmail] = useState("");
  const forgot = useForgotPassword();

  const submit = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    if (email.trim() === "") {
      return;
    }
    forgot.mutate({ data: { email: email.trim() } });
  };

  return (
    <div className="rounded-lg border border-border bg-card p-6">
      <h1 className="font-heading text-xl font-semibold text-foreground">{translate("title")}</h1>

      {forgot.isSuccess ? (
        <>
          <p className="mt-3 text-sm text-muted-foreground">{translate("sent")}</p>
          <Link
            href={ROUTES.login}
            className="mt-5 inline-block text-sm underline underline-offset-4"
          >
            {translate("backToLogin")}
          </Link>
        </>
      ) : (
        <>
          <p className="mt-1 text-sm text-muted-foreground">{translate("description")}</p>
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
                value={email}
                onChange={(event) => setEmail(event.target.value)}
                className={FIELD_CLASS}
              />
            </div>

            <button type="submit" disabled={forgot.isPending} className={PRIMARY_BUTTON_CLASS}>
              {forgot.isPending ? translate("sending") : translate("submit")}
            </button>

            {forgot.isError ? (
              <p role="alert" className="text-sm text-error">
                {translate("failed")}
              </p>
            ) : null}

            <Link href={ROUTES.login} className="text-sm underline underline-offset-4">
              {translate("backToLogin")}
            </Link>
          </form>
        </>
      )}
    </div>
  );
}
