"use client";

import { useTranslations } from "next-intl";
import type { AdminSpendRunResponse } from "@/src/api/generated/model";
import { FIELD_CLASS } from "@/src/components/form";
import { Notice } from "@/src/components/notice";
import { formatCompactNumber, formatUsd } from "@/src/lib/format";
import { formatTimestamp } from "@/src/lib/time";

// FixStatus (Condux.Core.FixEngine.FixStatus) -> an i18n key under admin.spend.statuses.
const STATUS_KEYS: Record<number, string> = {
  1: "pending",
  2: "running",
  3: "succeeded",
  4: "failed",
  5: "cancelled",
};

// The per-run drill-down: every Conductor run across all orgs (issue fixes + CVE bumps). The search box
// filters the loaded rows by org or model client-side (the list is bounded server-side).
export function AdminSpendRuns({
  runs,
  isPending,
  isError,
  search,
  onSearch,
}: {
  runs: AdminSpendRunResponse[];
  isPending: boolean;
  isError: boolean;
  search: string;
  onSearch: (value: string) => void;
}) {
  const translate = useTranslations("admin.spend");
  const query = search.trim().toLowerCase();
  const filtered = query
    ? runs.filter(
        (r) => r.orgName.toLowerCase().includes(query) || r.model.toLowerCase().includes(query),
      )
    : runs;

  return (
    <div className="flex flex-col gap-2">
      <div className="flex items-center justify-between gap-3">
        <h3 className="text-sm font-medium text-foreground">{translate("runsTitle")}</h3>
        <input
          value={search}
          onChange={(event) => onSearch(event.target.value)}
          placeholder={translate("searchPlaceholder")}
          className={`${FIELD_CLASS} w-56`}
        />
      </div>
      {isPending ? (
        <Notice>{translate("loading")}</Notice>
      ) : isError ? (
        <Notice>{translate("error")}</Notice>
      ) : filtered.length === 0 ? (
        <Notice>{translate("empty")}</Notice>
      ) : (
        <div className="overflow-x-auto rounded-lg border border-border">
          <table className="w-full text-left text-sm">
            <thead className="border-b border-border bg-card text-xs uppercase text-muted-foreground">
              <tr>
                <th className="px-4 py-2 font-medium">{translate("org")}</th>
                <th className="px-4 py-2 font-medium">{translate("model")}</th>
                <th className="px-4 py-2 font-medium">{translate("kind")}</th>
                <th className="px-4 py-2 font-medium">{translate("status")}</th>
                <th className="px-4 py-2 text-right font-medium">{translate("cost")}</th>
                <th className="px-4 py-2 font-medium">{translate("created")}</th>
              </tr>
            </thead>
            <tbody>
              {filtered.map((run) => (
                <tr key={run.id} className="border-b border-border last:border-0">
                  <td className="px-4 py-2 text-foreground">{run.orgName}</td>
                  <td className="px-4 py-2 text-foreground">{run.model}</td>
                  <td className="px-4 py-2 text-muted-foreground">
                    {translate(`kinds.${run.kind}`)}
                  </td>
                  <td className="px-4 py-2 text-muted-foreground">
                    {translate(`statuses.${STATUS_KEYS[run.status] ?? "pending"}`)}
                  </td>
                  <td className="px-4 py-2 text-right text-foreground tabular-nums">
                    {run.costUsd === null ? "—" : formatUsd(run.costUsd)}
                  </td>
                  <td className="px-4 py-2 text-muted-foreground">
                    {formatTimestamp(run.createdAt)}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      <p className="text-xs text-muted-foreground">
        {translate("tokensNote", {
          input: formatCompactNumber(filtered.reduce((n, r) => n + r.inputTokens, 0)),
          output: formatCompactNumber(filtered.reduce((n, r) => n + r.outputTokens, 0)),
        })}
      </p>
    </div>
  );
}
