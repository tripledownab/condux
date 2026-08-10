"use client";

import Link from "next/link";
import { useTranslations } from "next-intl";
import type { AdminSpendRollupResponse } from "@/src/api/generated/model";
import { formatCompactNumber, formatUsd } from "@/src/lib/format";
import { adminOrgDetailPath } from "@/src/routes";

// The spend rollup: headline totals, then per-model and per-org breakdowns. A null cost (a bring-your-own
// / unpriced model) shows a dash but still reports its tokens.
export function AdminSpendRollup({ rollup }: { rollup: AdminSpendRollupResponse }) {
  const translate = useTranslations("admin.spend");
  const cost = (value: number | null) => (value === null ? "—" : formatUsd(value));

  return (
    <div className="flex flex-col gap-6">
      <dl className="grid grid-cols-2 gap-4 sm:grid-cols-4">
        <Stat label={translate("total")} value={formatUsd(rollup.totalUsd)} />
        <Stat label={translate("runs")} value={formatCompactNumber(rollupRuns(rollup))} />
        <Stat
          label={translate("inputTokens")}
          value={formatCompactNumber(sum(rollup, "inputTokens"))}
        />
        <Stat
          label={translate("outputTokens")}
          value={formatCompactNumber(sum(rollup, "outputTokens"))}
        />
      </dl>

      <Table
        caption={translate("byModel")}
        head={[
          translate("model"),
          translate("runs"),
          translate("inputTokens"),
          translate("outputTokens"),
          translate("cost"),
        ]}
      >
        {rollup.byModel.map((row) => (
          <tr key={row.model} className="border-b border-border last:border-0">
            <td className="px-4 py-2 text-foreground">{row.model}</td>
            <td className="px-4 py-2 text-right text-foreground tabular-nums">{row.runCount}</td>
            <td className="px-4 py-2 text-right text-foreground tabular-nums">
              {formatCompactNumber(row.inputTokens)}
            </td>
            <td className="px-4 py-2 text-right text-foreground tabular-nums">
              {formatCompactNumber(row.outputTokens)}
            </td>
            <td className="px-4 py-2 text-right text-foreground tabular-nums">
              {cost(row.costUsd)}
            </td>
          </tr>
        ))}
      </Table>

      <Table
        caption={translate("byOrg")}
        head={[translate("org"), translate("runs"), translate("cost")]}
      >
        {rollup.byOrg.map((row) => (
          <tr key={row.orgId} className="border-b border-border last:border-0">
            <td className="px-4 py-2 text-foreground">
              <Link href={adminOrgDetailPath(row.orgId)} className="hover:underline">
                {row.orgName}
              </Link>
            </td>
            <td className="px-4 py-2 text-right text-foreground tabular-nums">{row.runCount}</td>
            <td className="px-4 py-2 text-right text-foreground tabular-nums">
              {cost(row.costUsd)}
            </td>
          </tr>
        ))}
      </Table>
    </div>
  );
}

const rollupRuns = (r: AdminSpendRollupResponse) => r.byModel.reduce((n, m) => n + m.runCount, 0);
const sum = (r: AdminSpendRollupResponse, key: "inputTokens" | "outputTokens") =>
  r.byModel.reduce((n, m) => n + m[key], 0);

function Stat({ label, value }: { label: string; value: string }) {
  return (
    <div className="rounded-lg border border-border bg-card p-4">
      <dt className="text-xs uppercase text-muted-foreground">{label}</dt>
      <dd className="mt-1 font-heading text-2xl font-semibold text-foreground tabular-nums">
        {value}
      </dd>
    </div>
  );
}

function Table({
  caption,
  head,
  children,
}: {
  caption: string;
  head: string[];
  children: React.ReactNode;
}) {
  return (
    <div>
      <h3 className="mb-2 text-sm font-medium text-foreground">{caption}</h3>
      <div className="overflow-x-auto rounded-lg border border-border">
        <table className="w-full text-left text-sm">
          <thead className="border-b border-border bg-card text-xs uppercase text-muted-foreground">
            <tr>
              {head.map((label, index) => (
                <th
                  key={label}
                  className={`px-4 py-2 font-medium ${index === 0 ? "" : "text-right"}`}
                >
                  {label}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>{children}</tbody>
        </table>
      </div>
    </div>
  );
}
