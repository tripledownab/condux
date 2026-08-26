"use client";

import { useTranslations } from "next-intl";
import { useState } from "react";
import { useAdminSpendRollup, useAdminSpendRuns } from "@/src/api/generated/condux";
import { Notice } from "@/src/components/notice";
import { Combobox } from "@/src/components/ui/combobox";
import { AdminSpendRollup } from "./admin-spend-rollup";
import { AdminSpendRuns } from "./admin-spend-runs";

const WINDOWS = [7, 30, 90];

// The AI Spend tab (ADR-0027): cross-org Conductor spend over a time window — a rollup (total + per-model
// + per-org) and a per-run drill-down. The orchestrator owns the window + search state and fetches once,
// passing results to the presentational children.
export function AdminSpend() {
  const translate = useTranslations("admin.spend");
  const tCommon = useTranslations("common");
  const [days, setDays] = useState(30);
  const [search, setSearch] = useState("");
  const rollup = useAdminSpendRollup({ days });
  const runs = useAdminSpendRuns({ days });
  const windowOptions = WINDOWS.map((option) => ({
    value: String(option),
    label: translate("window.days", { count: option }),
  }));

  return (
    <div className="flex flex-col gap-6">
      <div className="flex items-center gap-2 text-sm text-muted-foreground">
        <span>{translate("window.label")}</span>
        <Combobox
          value={String(days)}
          onValueChange={(value) => setDays(Number(value))}
          options={windowOptions}
          aria-label={translate("window.label")}
          className="w-40"
          searchPlaceholder={tCommon("comboboxSearch")}
          emptyText={tCommon("comboboxEmpty")}
        />
      </div>

      {rollup.isPending ? (
        <Notice>{translate("loading")}</Notice>
      ) : rollup.isError || !rollup.data?.data ? (
        <Notice>{translate("error")}</Notice>
      ) : (
        <AdminSpendRollup rollup={rollup.data.data} />
      )}

      <AdminSpendRuns
        runs={runs.data?.data ?? []}
        isPending={runs.isPending}
        isError={runs.isError}
        search={search}
        onSearch={setSearch}
      />
    </div>
  );
}
