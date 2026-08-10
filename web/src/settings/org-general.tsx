"use client";

import { useQueryClient } from "@tanstack/react-query";
import { useTranslations } from "next-intl";
import { useState } from "react";
import type { ConduxApiError } from "@/src/api/fetcher";
import { getListMyOrgsQueryKey, useAiFixUsage, useUpdateOrg } from "@/src/api/generated/condux";
import { Notice } from "@/src/components/notice";
import { formatUsd } from "@/src/lib/format";
import { OrgStatus, useCurrentOrg } from "@/src/orgs/current-org";
import { tierIsByo } from "@/src/orgs/plan-format";
import { PlanSection } from "./plan-section";

// Org AI-fix modes (#101), matching Condux.Core.FixEngine.AiFixMode.
const MANUAL = 0;
const AUTO = 1;

// The General settings tab: the current org's identity, its plan, and its AI-fix mode. Rename/delete and
// self-serve plan changes arrive with the admin + billing work; the rest is a read-only overview.
export function OrgGeneral() {
  const translate = useTranslations("settings.general");
  const current = useCurrentOrg();

  if (current.status === OrgStatus.Loading) {
    return <Notice>{translate("loading")}</Notice>;
  }
  if (current.status === OrgStatus.Error) {
    return <Notice>{translate("error")}</Notice>;
  }
  if (current.status === OrgStatus.NoOrg) {
    return <Notice>{translate("noOrg")}</Notice>;
  }

  const { org, role } = current;
  const canManage = role === "owner" || role === "admin";
  return (
    <div className="flex flex-col gap-8">
      <section>
        <h2 className="font-heading text-lg font-semibold text-foreground">
          {translate("orgTitle")}
        </h2>
        <dl className="mt-3 grid grid-cols-2 gap-x-6 gap-y-3 text-sm sm:grid-cols-3">
          <Field label={translate("name")} value={org.name} />
          <Field label={translate("slug")} value={org.slug} />
          <Field label={translate("yourRole")} value={role} />
        </dl>
      </section>
      <PlanSection orgId={org.id} tier={org.tier} canManage={canManage} />
      <AiFixSettings
        orgId={org.id}
        tier={org.tier}
        mode={org.aiFixMode}
        costCapUsd={org.aiFixCostCapUsd}
        canManage={canManage}
      />
    </div>
  );
}

// The org's Conductor settings (#101/#120): manual vs auto fixes and an optional monthly spend cap. Both
// are one PATCH, so each control submits the current value of the other (a mode toggle must not wipe the
// cap). Admin+ can change them; switching to auto needs a plan that allows it (the API 409s otherwise —
// Free includes fix runs but stays manual, so the refusal is about auto, not about having an allowance).
function AiFixSettings({
  orgId,
  tier,
  mode,
  costCapUsd,
  canManage,
}: {
  orgId: number;
  tier: number;
  mode: number;
  costCapUsd: number | null;
  canManage: boolean;
}) {
  const translate = useTranslations("settings.general");
  const queryClient = useQueryClient();
  const update = useUpdateOrg();
  const usage = useAiFixUsage(orgId, { query: { enabled: canManage } });
  const [capInput, setCapInput] = useState(costCapUsd === null ? "" : String(costCapUsd));
  // BYO (Enterprise) sets a real budget on their own key; every platform-billed tier, Free included,
  // sees a read-only fair-use compute ceiling (the plan default, ADR-0020/0027).
  const isByo = tierIsByo(tier);

  const save = (nextMode: number, nextCap: number | null) => {
    update.mutate(
      { orgId, data: { aiFixMode: nextMode, aiFixCostCapUsd: nextCap } },
      {
        onSuccess: () => {
          queryClient.invalidateQueries({ queryKey: getListMyOrgsQueryKey() });
          usage.refetch();
        },
      },
    );
  };

  const selectMode = (next: number) => {
    if (next !== mode && !update.isPending) {
      save(next, costCapUsd); // preserve the saved cap when toggling mode
    }
  };

  const saveCap = () => {
    const trimmed = capInput.trim();
    const nextCap = trimmed === "" ? null : Number(trimmed);
    if (nextCap !== null && (Number.isNaN(nextCap) || nextCap < 0)) {
      return; // ignore an invalid entry; the backend also rejects it
    }
    save(mode, nextCap);
  };

  const errorKey =
    (update.error as ConduxApiError | null)?.status === 409 ? "aiFixUpgrade" : "aiFixFailed";
  const spent = usage.data?.data.monthToDateUsd ?? 0;
  // The effective ceiling (org override or plan default) and remaining allowance, from the usage meter.
  const effectiveCap = usage.data?.data.capUsd ?? null;
  const remaining = usage.data?.data.remainingFixes ?? null;

  return (
    <section>
      <h2 className="font-heading text-lg font-semibold text-foreground">
        {translate("aiFixTitle")}
      </h2>
      <p className="mt-1 text-sm text-muted-foreground">{translate("aiFixDescription")}</p>
      {canManage ? (
        <fieldset className="mt-3 flex flex-col gap-2" disabled={update.isPending}>
          <ModeOption
            checked={mode === MANUAL}
            onSelect={() => selectMode(MANUAL)}
            label={translate("aiFixManual")}
            hint={translate("aiFixManualHint")}
          />
          <ModeOption
            checked={mode === AUTO}
            onSelect={() => selectMode(AUTO)}
            label={translate("aiFixAuto")}
            hint={translate("aiFixAutoHint")}
          />
        </fieldset>
      ) : (
        <p className="mt-2 text-sm text-foreground">
          {mode === AUTO ? translate("aiFixAuto") : translate("aiFixManual")}
        </p>
      )}

      <h3 className="mt-6 text-sm font-medium text-foreground">
        {isByo ? translate("budgetTitle") : translate("computeLimitTitle")}
      </h3>
      <p className="mt-1 text-sm text-muted-foreground">
        {isByo ? translate("budgetDescription") : translate("computeLimitDescription")}
      </p>
      {/* Only BYO orgs set their own budget; the platform tiers' ceiling is Condux-owned (read-only). */}
      {isByo && canManage ? (
        <div className="mt-2 flex items-end gap-2">
          <label className="flex flex-col gap-1 text-xs text-muted-foreground">
            {translate("budgetLabel")}
            <input
              type="number"
              min={0}
              step="1"
              inputMode="decimal"
              value={capInput}
              onChange={(event) => setCapInput(event.target.value)}
              placeholder={translate("costCapPlaceholder")}
              className="w-32 rounded-md border border-border bg-card px-2 py-1.5 text-sm text-foreground"
            />
          </label>
          <button
            type="button"
            onClick={saveCap}
            disabled={update.isPending}
            className="rounded-md border border-border bg-card px-3 py-1.5 text-sm font-medium text-foreground hover:border-ring disabled:opacity-50"
          >
            {translate("costCapSave")}
          </button>
        </div>
      ) : null}
      <p className="mt-2 text-xs text-muted-foreground tabular-nums">
        {effectiveCap !== null
          ? translate("costCapSpentOfCap", {
              amount: formatUsd(spent),
              cap: formatUsd(effectiveCap),
            })
          : translate("costCapSpent", { amount: formatUsd(spent) })}
      </p>
      {!isByo && typeof remaining === "number" ? (
        <p className="mt-1 text-xs text-muted-foreground tabular-nums">
          {translate("fixesRemaining", { count: remaining })}
        </p>
      ) : null}

      {update.isError ? (
        <p role="alert" className="mt-2 text-sm text-error">
          {translate(errorKey)}
        </p>
      ) : null}
    </section>
  );
}

function ModeOption({
  checked,
  onSelect,
  label,
  hint,
}: {
  checked: boolean;
  onSelect: () => void;
  label: string;
  hint: string;
}) {
  return (
    <label className="flex cursor-pointer items-start gap-3 rounded-md border border-border bg-card p-3 text-sm hover:border-ring">
      <input
        type="radio"
        name="ai-fix-mode"
        checked={checked}
        onChange={onSelect}
        className="mt-0.5"
      />
      <span>
        <span className="font-medium text-foreground">{label}</span>
        <span className="mt-0.5 block text-xs text-muted-foreground">{hint}</span>
      </span>
    </label>
  );
}

function Field({ label, value }: { label: string; value: string }) {
  return (
    <div>
      <dt className="text-xs uppercase text-muted-foreground">{label}</dt>
      <dd className="mt-0.5 text-foreground">{value}</dd>
    </div>
  );
}
