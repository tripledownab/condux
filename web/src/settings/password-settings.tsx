"use client";

import { useTranslations } from "next-intl";
import { type FormEvent, useId, useState } from "react";
import type { ConduxApiError } from "@/src/api/fetcher";
import { useChangePassword } from "@/src/api/generated/condux";
import { FIELD_CLASS, PRIMARY_BUTTON_CLASS } from "@/src/components/form";

/**
 * Changing the signed-in user's own password. Account-level like the MFA panel beside it, not
 * organisation-level.
 *
 * The confirmation field is not decoration: with no way to recover a forgotten password, a mistyped new
 * one would lock the account exactly as a mistyped signup does. It is checked here rather than at the
 * API, because the server has no way to tell a typo from an intended value.
 *
 * A federated account (Google, SSO) has no password to change, and the API answers 401 because there is
 * no current password to verify. That reads as "wrong password", so the panel says which it is.
 */
export function PasswordSettings() {
  const translate = useTranslations("settings.password");
  const currentId = useId();
  const nextId = useId();
  const confirmId = useId();
  const [current, setCurrent] = useState("");
  const [next, setNext] = useState("");
  const [confirm, setConfirm] = useState("");
  const [done, setDone] = useState(false);
  const change = useChangePassword();

  const mismatch = confirm !== "" && confirm !== next;
  const canSubmit = current !== "" && next.length >= 8 && confirm === next && !change.isPending;

  const submit = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    if (!canSubmit) {
      return;
    }
    setDone(false);
    change.mutate(
      { data: { currentPassword: current, newPassword: next } },
      {
        onSuccess: () => {
          setCurrent("");
          setNext("");
          setConfirm("");
          setDone(true);
        },
      },
    );
  };

  const status = change.error as ConduxApiError | null;
  const errorKey = change.isError ? (status?.status === 401 ? "wrongPassword" : "failed") : null;

  return (
    <section className="rounded-lg border border-border bg-card p-6">
      <h2 className="font-heading text-lg font-semibold text-foreground">{translate("title")}</h2>
      <p className="mt-1 text-sm text-muted-foreground">{translate("description")}</p>

      <form onSubmit={submit} className="mt-4 flex max-w-sm flex-col gap-4" noValidate>
        <div className="flex flex-col gap-1.5">
          <label htmlFor={currentId} className="text-sm text-muted-foreground">
            {translate("current")}
          </label>
          <input
            id={currentId}
            type="password"
            autoComplete="current-password"
            value={current}
            onChange={(event) => setCurrent(event.target.value)}
            className={FIELD_CLASS}
          />
        </div>

        <div className="flex flex-col gap-1.5">
          <label htmlFor={nextId} className="text-sm text-muted-foreground">
            {translate("next")}
          </label>
          <input
            id={nextId}
            type="password"
            minLength={8}
            autoComplete="new-password"
            value={next}
            onChange={(event) => setNext(event.target.value)}
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
          {change.isPending ? translate("saving") : translate("submit")}
        </button>

        {errorKey ? (
          <p role="alert" className="text-sm text-error">
            {translate(errorKey)}
          </p>
        ) : null}
        {done ? <p className="text-sm text-muted-foreground">{translate("changed")}</p> : null}
      </form>
    </section>
  );
}
