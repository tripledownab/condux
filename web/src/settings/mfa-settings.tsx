"use client";

import { useTranslations } from "next-intl";
import QRCode from "qrcode";
import { useEffect, useId, useState } from "react";
import {
  useConfirmMfa,
  useDisableMfa,
  useEnrollMfa,
  useMfaStatus,
  useRegenerateRecoveryCodes,
} from "@/src/api/generated/condux";
import { FIELD_CLASS, PRIMARY_BUTTON_CLASS } from "@/src/components/form";
import { Notice } from "@/src/components/notice";

/**
 * Two-factor authentication for the signed-in user's own account.
 *
 * This is account-level, not organisation-level, unlike every other settings tab. It lives here because
 * it is where a user looks for it; the data it touches belongs to the person and follows them between
 * organisations.
 */
export function MfaSettings() {
  const translate = useTranslations("auth.mfa");
  const status = useMfaStatus();
  const [password, setPassword] = useState("");
  const [enrolment, setEnrolment] = useState<{ secret: string; uri: string } | null>(null);
  const [codes, setCodes] = useState<string[] | null>(null);

  // orval returns a discriminated wrapper, so narrow on the status before reading the body.
  const current = status.data?.status === 200 ? status.data.data : null;

  if (status.isPending) return <Notice>{translate("loading")}</Notice>;
  if (current && !current.available) {
    // Named rather than hidden: a missing server key is an operator problem, and a silently absent
    // feature reads as "this product has no MFA".
    return <Notice>{translate("unavailable")}</Notice>;
  }

  if (codes) {
    return (
      <RecoveryCodeList
        codes={codes}
        onDone={() => {
          setCodes(null);
          status.refetch();
        }}
      />
    );
  }

  if (enrolment) {
    return (
      <EnrolmentSteps
        enrolment={enrolment}
        password={password}
        onConfirmed={(issued) => {
          setEnrolment(null);
          setPassword("");
          setCodes(issued);
        }}
      />
    );
  }

  return current?.enabled ? (
    <EnabledPanel
      password={password}
      setPassword={setPassword}
      remaining={current.remainingRecoveryCodes}
      onChanged={() => {
        setPassword("");
        status.refetch();
      }}
      onRegenerated={setCodes}
    />
  ) : (
    <DisabledPanel password={password} setPassword={setPassword} onStarted={setEnrolment} />
  );
}

function DisabledPanel({
  password,
  setPassword,
  onStarted,
}: {
  password: string;
  setPassword: (value: string) => void;
  onStarted: (enrolment: { secret: string; uri: string }) => void;
}) {
  const translate = useTranslations("auth.mfa");
  const passwordId = useId();
  const enroll = useEnrollMfa();

  return (
    <section className="space-y-4 rounded-lg border border-border bg-card p-6">
      <div>
        <h2 className="font-heading text-lg">{translate("title")}</h2>
        <p className="mt-1 text-sm text-muted-foreground">{translate("description")}</p>
      </div>

      <div>
        <label className="block text-sm" htmlFor={passwordId}>
          {translate("confirmPassword")}
        </label>
        <input
          id={passwordId}
          className={FIELD_CLASS}
          type="password"
          autoComplete="current-password"
          value={password}
          onChange={(event) => setPassword(event.target.value)}
        />
      </div>

      {enroll.isError ? (
        <p role="alert" className="text-sm text-destructive">
          {translate("errors.wrongPassword")}
        </p>
      ) : null}

      <button
        className={PRIMARY_BUTTON_CLASS}
        type="button"
        disabled={!password || enroll.isPending}
        onClick={() =>
          enroll.mutate(
            { data: { password } },
            {
              onSuccess: (result) => {
                if (result.status === 200) onStarted(result.data);
              },
            },
          )
        }
      >
        {translate("enable")}
      </button>
    </section>
  );
}

function EnrolmentSteps({
  enrolment,
  password,
  onConfirmed,
}: {
  enrolment: { secret: string; uri: string };
  password: string;
  onConfirmed: (codes: string[]) => void;
}) {
  const translate = useTranslations("auth.mfa");
  const codeId = useId();
  const [code, setCode] = useState("");
  const [qr, setQr] = useState<string | null>(null);
  const confirm = useConfirmMfa();

  // Rendered in the browser, so the secret is never sent anywhere to be drawn into an image.
  useEffect(() => {
    QRCode.toDataURL(enrolment.uri, { margin: 1, width: 200 })
      .then(setQr)
      .catch(() => setQr(null));
  }, [enrolment.uri]);

  return (
    <section className="space-y-4 rounded-lg border border-border bg-card p-6">
      <h2 className="font-heading text-lg">{translate("setupTitle")}</h2>
      <p className="text-sm text-muted-foreground">{translate("setupStep1")}</p>

      {/* Decorative: the same secret is below as text, which is what a screen reader gets. */}
      {/* next/image cannot optimise a data URL built in the browser, and routing it through the loader
          would defeat the point of never sending the secret anywhere to be drawn. */}
      {qr ? (
        // biome-ignore lint/performance/noImgElement: a client-generated data URL
        <img src={qr} alt="" width={200} height={200} className="rounded bg-white p-2" />
      ) : null}

      <div>
        <p className="text-sm text-muted-foreground">{translate("manualEntry")}</p>
        <code className="mt-1 block break-all rounded bg-muted px-2 py-1 font-mono text-sm">
          {enrolment.secret}
        </code>
      </div>

      <div>
        <label className="block text-sm" htmlFor={codeId}>
          {translate("setupStep2")}
        </label>
        <input
          id={codeId}
          className={FIELD_CLASS}
          type="text"
          inputMode="numeric"
          autoComplete="one-time-code"
          value={code}
          onChange={(event) => setCode(event.target.value)}
        />
      </div>

      {confirm.isError ? (
        <p role="alert" className="text-sm text-destructive">
          {translate("errors.invalidCode")}
        </p>
      ) : null}

      <button
        className={PRIMARY_BUTTON_CLASS}
        type="button"
        disabled={!code || confirm.isPending}
        onClick={() =>
          confirm.mutate(
            { data: { password, code } },
            {
              onSuccess: (result) => {
                if (result.status === 200) onConfirmed(result.data.codes);
              },
            },
          )
        }
      >
        {translate("confirm")}
      </button>
    </section>
  );
}

function RecoveryCodeList({ codes, onDone }: { codes: string[]; onDone: () => void }) {
  const translate = useTranslations("auth.mfa");
  const [saved, setSaved] = useState(false);

  return (
    <section className="space-y-4 rounded-lg border border-border bg-card p-6">
      <h2 className="font-heading text-lg">{translate("recoveryTitle")}</h2>
      <Notice>{translate("recoveryWarning")}</Notice>

      <ul className="grid grid-cols-2 gap-2 font-mono text-sm">
        {codes.map((code) => (
          <li key={code}>{code}</li>
        ))}
      </ul>

      <div className="flex gap-2">
        <button
          className={PRIMARY_BUTTON_CLASS}
          type="button"
          onClick={() => navigator.clipboard.writeText(codes.join("\n"))}
        >
          {translate("copyAll")}
        </button>
        <button
          className={PRIMARY_BUTTON_CLASS}
          type="button"
          onClick={() => {
            // A download as well as a copy: a clipboard is lost on the next copy, and these are the
            // only way back in if the phone is gone.
            const url = URL.createObjectURL(new Blob([codes.join("\n")], { type: "text/plain" }));
            const link = document.createElement("a");
            link.href = url;
            link.download = "condux-recovery-codes.txt";
            link.click();
            URL.revokeObjectURL(url);
          }}
        >
          {translate("download")}
        </button>
      </div>

      <label className="flex items-center gap-2 text-sm">
        <input
          type="checkbox"
          checked={saved}
          onChange={(event) => setSaved(event.target.checked)}
        />
        {translate("savedConfirmation")}
      </label>

      <button className={PRIMARY_BUTTON_CLASS} type="button" disabled={!saved} onClick={onDone}>
        {translate("done")}
      </button>
    </section>
  );
}

function EnabledPanel({
  password,
  setPassword,
  remaining,
  onChanged,
  onRegenerated,
}: {
  password: string;
  setPassword: (value: string) => void;
  remaining: number;
  onChanged: () => void;
  onRegenerated: (codes: string[]) => void;
}) {
  const translate = useTranslations("auth.mfa");
  const passwordId = useId();
  const disable = useDisableMfa();
  const regenerate = useRegenerateRecoveryCodes();

  return (
    <section className="space-y-4 rounded-lg border border-border bg-card p-6">
      <div>
        <h2 className="font-heading text-lg">{translate("title")}</h2>
        <p className="mt-1 text-sm text-muted-foreground">{translate("enabled")}</p>
        <p className="mt-1 text-sm text-muted-foreground">
          {translate("recoveryRemaining", { count: remaining })}
        </p>
      </div>

      <div>
        <label className="block text-sm" htmlFor={passwordId}>
          {translate("confirmPassword")}
        </label>
        <input
          id={passwordId}
          className={FIELD_CLASS}
          type="password"
          autoComplete="current-password"
          value={password}
          onChange={(event) => setPassword(event.target.value)}
        />
      </div>

      {disable.isError || regenerate.isError ? (
        <p role="alert" className="text-sm text-destructive">
          {translate("errors.wrongPassword")}
        </p>
      ) : null}

      <div className="flex gap-2">
        <button
          className={PRIMARY_BUTTON_CLASS}
          type="button"
          disabled={!password || regenerate.isPending}
          onClick={() =>
            regenerate.mutate(
              { data: { password } },
              {
                onSuccess: (result) => {
                  if (result.status === 200) onRegenerated(result.data.codes);
                },
              },
            )
          }
        >
          {translate("regenerate")}
        </button>
        <button
          className={PRIMARY_BUTTON_CLASS}
          type="button"
          disabled={!password || disable.isPending}
          onClick={() => disable.mutate({ data: { password } }, { onSuccess: onChanged })}
        >
          {translate("disable")}
        </button>
      </div>
    </section>
  );
}
