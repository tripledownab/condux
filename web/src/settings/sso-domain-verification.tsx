"use client";

import { useQueryClient } from "@tanstack/react-query";
import { useFormatter, useTranslations } from "next-intl";
import type { ConduxApiError } from "@/src/api/fetcher";
import { getGetSsoConfigQueryKey, useVerifySsoDomain } from "@/src/api/generated/condux";
import type { SsoConfigResponse } from "@/src/api/generated/model";
import { SECONDARY_BUTTON_CLASS } from "@/src/components/form";

// Proving that the org controls the domain its SSO config claims (ADR-0043). Its own component because
// naming a domain and proving one are separate acts the org does minutes or days apart, and because the
// settings form it sits beside is already long.

// The wire values of DomainCheckOutcome, which is why they are strings rather than an enum ordinal: an
// ordinal would re-map silently if a case were ever inserted server-side.
const OUTCOME_MESSAGE: Record<string, string> = {
  verified: "verifyVerified",
  record_missing: "verifyRecordMissing",
  resolver_unavailable: "verifyResolverUnavailable",
};

// Four states, because the daily re-check (ADR-0043 slice 2) can take a proof away again. Failing and
// Lapsed are kept apart from Pending on purpose: an org whose record went missing has done the work once,
// and telling it "Not verified" reads as though it never did.
enum DomainState {
  Verified = "verified",
  Failing = "failing",
  Lapsed = "lapsed",
  Pending = "pending",
}

function domainStateOf(config: SsoConfigResponse): DomainState {
  if (config.verifiedAt) {
    return config.verificationLostAt ? DomainState.Failing : DomainState.Verified;
  }
  return config.verificationLostAt ? DomainState.Lapsed : DomainState.Pending;
}

// Failing and Lapsed are both the org's SSO in trouble, so they read as an error. Pending is not an error:
// nobody has done anything wrong yet.
const STATE_CLASS: Record<DomainState, string> = {
  [DomainState.Verified]: "text-sm text-foreground",
  [DomainState.Failing]: "text-sm text-error",
  [DomainState.Lapsed]: "text-sm text-error",
  [DomainState.Pending]: "text-sm text-muted-foreground",
};

// A refusal, as opposed to a check that ran. Both are 409s and the generic message fits neither: one says
// somebody else proved this domain first, the other that the config moved under the open page.
function verifyErrorKey(error: unknown): string {
  const code = (error as ConduxApiError | null)?.code;
  if (code === "email_domain_taken") {
    return "domainTaken";
  }
  if (code === "sso_config_changed") {
    return "verifyConfigChanged";
  }
  return "verifyFailed";
}

export function SsoDomainVerification({
  orgId,
  config,
  canManage,
}: {
  orgId: number;
  config: SsoConfigResponse;
  canManage: boolean;
}) {
  const translate = useTranslations("settings.sso");
  const format = useFormatter();
  const queryClient = useQueryClient();
  const verify = useVerifySsoDomain();

  const state = domainStateOf(config);
  const outcome = verify.data?.status === 200 ? verify.data.data.outcome : null;
  const day = (value: string) =>
    format.dateTime(new Date(value), { month: "short", day: "numeric", year: "numeric" });

  return (
    <section className="flex flex-col gap-3 rounded-md border border-border p-4">
      <div className="flex items-center justify-between gap-4">
        <h3 className="font-heading text-sm font-semibold text-foreground">
          {translate("verifyTitle")}
        </h3>
        <span className={STATE_CLASS[state]}>
          {state === DomainState.Verified && config.verifiedAt
            ? translate("verifyStateVerified", { date: day(config.verifiedAt) })
            : null}
          {state === DomainState.Failing && config.verificationLostAt
            ? translate("verifyStateFailing", { date: day(config.verificationLostAt) })
            : null}
          {state === DomainState.Lapsed ? translate("verifyStateLapsed") : null}
          {state === DomainState.Pending ? translate("verifyStatePending") : null}
        </span>
      </div>

      {/* Shown whether or not the claim is proved. An org that has to move the record, or rebuild a zone,
          needs to read the value it published just as much as one setting it up for the first time. */}
      <dl className="grid grid-cols-[auto_1fr] gap-x-6 gap-y-1.5 text-sm">
        <dt className="text-muted-foreground">{translate("verifyRecordTypeLabel")}</dt>
        <dd className="text-foreground">TXT</dd>
        <dt className="text-muted-foreground">{translate("verifyRecordNameLabel")}</dt>
        <dd className="break-all font-mono text-foreground">{config.verificationRecordName}</dd>
        <dt className="text-muted-foreground">{translate("verifyRecordValueLabel")}</dt>
        <dd className="break-all font-mono text-foreground">{config.verificationRecordValue}</dd>
      </dl>

      {state === DomainState.Pending ? (
        <p className="text-sm text-muted-foreground">{translate("verifyPendingHelp")}</p>
      ) : null}
      {/* The deadline comes from the server, which owns the grace period, rather than from arithmetic
          here that would be free to drift from the one the org was emailed. Guarded on the date like the
          lapsed line below: the server sends it with exactly this state, and inventing a sentence for a
          state it cannot send would be a branch only a test could ever reach. */}
      {state === DomainState.Failing && config.verificationLapsesAt ? (
        <p className="text-sm text-error">
          {translate("verifyFailingHelp", { date: day(config.verificationLapsesAt) })}
        </p>
      ) : null}
      {state === DomainState.Lapsed && config.verificationLostAt ? (
        <p className="text-sm text-error">
          {translate("verifyLapsedHelp", { date: day(config.verificationLostAt) })}
        </p>
      ) : null}

      {canManage ? (
        <div className="flex items-center gap-3">
          <button
            type="button"
            disabled={verify.isPending}
            onClick={() =>
              verify.mutate(
                { orgId },
                {
                  onSuccess: () =>
                    queryClient.invalidateQueries({ queryKey: getGetSsoConfigQueryKey(orgId) }),
                },
              )
            }
            className={SECONDARY_BUTTON_CLASS}
          >
            {verify.isPending ? translate("verifying") : translate("verify")}
          </button>
          {/* A failed request and a check that answered "not yet" are different things, so they are not
              reported with one message. The second is the ordinary case and is not an error. */}
          {outcome !== null ? (
            <span
              role="status"
              className={outcome === "verified" ? "text-sm text-foreground" : "text-sm text-error"}
            >
              {translate(OUTCOME_MESSAGE[outcome] ?? "verifyFailed")}
            </span>
          ) : null}
          {verify.isError ? (
            <span role="alert" className="text-sm text-error">
              {translate(verifyErrorKey(verify.error))}
            </span>
          ) : null}
        </div>
      ) : null}
    </section>
  );
}
