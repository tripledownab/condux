"use client";

import { useQueryClient } from "@tanstack/react-query";
import { useTranslations } from "next-intl";
import { useState } from "react";
import {
  getAdminListOrgsQueryKey,
  getAdminOrgDetailQueryKey,
  useAdminUpdateOrg,
} from "@/src/api/generated/condux";
import { FIELD_CLASS, PRIMARY_BUTTON_CLASS, SECONDARY_BUTTON_CLASS } from "@/src/components/form";
import { planName } from "@/src/orgs/plan-format";

// Org AI-fix modes (Condux.Core.FixEngine.AiFixMode).
const MANUAL = 0;
const AUTO = 1;

// Editable org identity + AI-fix settings for the admin detail page (ADR-0027). Name, mode, and cap are
// one PATCH, so every control submits the saved value of the others (a mode toggle must not wipe the
// name or cap). The plan tier is shown read-only here since it is Stripe/webhook owned (see billing).
export function AdminOrgInfoCard({
  orgId,
  name,
  slug,
  tier,
  aiFixMode,
  aiFixCostCapUsd,
}: {
  orgId: number;
  name: string;
  slug: string;
  tier: number;
  aiFixMode: number;
  aiFixCostCapUsd: number | null;
}) {
  const translate = useTranslations("admin.orgDetail");
  const queryClient = useQueryClient();
  const update = useAdminUpdateOrg();
  const [nameInput, setNameInput] = useState(name);
  const [capInput, setCapInput] = useState(aiFixCostCapUsd === null ? "" : String(aiFixCostCapUsd));

  const save = (nextName: string, nextMode: number, nextCap: number | null) => {
    update.mutate(
      { orgId, data: { name: nextName, aiFixMode: nextMode, aiFixCostCapUsd: nextCap } },
      {
        onSuccess: () => {
          queryClient.invalidateQueries({ queryKey: getAdminOrgDetailQueryKey(orgId) });
          queryClient.invalidateQueries({ queryKey: getAdminListOrgsQueryKey() });
        },
      },
    );
  };

  const saveName = () => {
    const trimmed = nameInput.trim();
    if (trimmed !== "" && !update.isPending) {
      save(trimmed, aiFixMode, aiFixCostCapUsd);
    }
  };

  const selectMode = (next: number) => {
    if (next !== aiFixMode && !update.isPending) {
      save(name, next, aiFixCostCapUsd);
    }
  };

  const saveCap = () => {
    const trimmed = capInput.trim();
    const nextCap = trimmed === "" ? null : Number(trimmed);
    if (nextCap !== null && (Number.isNaN(nextCap) || nextCap < 0)) {
      return; // the backend rejects it too
    }
    save(name, aiFixMode, nextCap);
  };

  return (
    <section className="flex flex-col gap-4">
      <h2 className="font-heading text-lg font-semibold text-foreground">{translate("title")}</h2>

      <div className="flex items-end gap-2">
        <label className="flex flex-1 flex-col gap-1.5 text-sm text-muted-foreground">
          {translate("nameLabel")}
          <input
            value={nameInput}
            onChange={(event) => setNameInput(event.target.value)}
            className={FIELD_CLASS}
          />
        </label>
        <button
          type="button"
          onClick={saveName}
          disabled={update.isPending}
          className={PRIMARY_BUTTON_CLASS}
        >
          {translate("save")}
        </button>
      </div>
      <p className="text-xs text-muted-foreground">
        {translate("slugAndPlan", { slug, plan: planName(tier) })}
      </p>

      <div>
        <h3 className="text-sm font-medium text-foreground">{translate("aiFixTitle")}</h3>
        <fieldset className="mt-2 flex flex-col gap-2" disabled={update.isPending}>
          <ModeOption
            checked={aiFixMode === MANUAL}
            onSelect={() => selectMode(MANUAL)}
            label={translate("aiFixManual")}
          />
          <ModeOption
            checked={aiFixMode === AUTO}
            onSelect={() => selectMode(AUTO)}
            label={translate("aiFixAuto")}
          />
        </fieldset>
      </div>

      <div>
        <h3 className="text-sm font-medium text-foreground">{translate("costCapTitle")}</h3>
        <div className="mt-2 flex items-end gap-2">
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
          <button
            type="button"
            onClick={saveCap}
            disabled={update.isPending}
            className={SECONDARY_BUTTON_CLASS}
          >
            {translate("save")}
          </button>
        </div>
      </div>

      {update.isError ? (
        <p role="alert" className="text-sm text-error">
          {translate("saveFailed")}
        </p>
      ) : null}
    </section>
  );
}

function ModeOption({
  checked,
  onSelect,
  label,
}: {
  checked: boolean;
  onSelect: () => void;
  label: string;
}) {
  return (
    <label className="flex cursor-pointer items-center gap-3 rounded-md border border-border bg-card p-3 text-sm hover:border-ring">
      <input type="radio" name="admin-ai-fix-mode" checked={checked} onChange={onSelect} />
      <span className="font-medium text-foreground">{label}</span>
    </label>
  );
}
