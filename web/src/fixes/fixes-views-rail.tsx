"use client";

import { useTranslations } from "next-intl";
import type { FixCostRollup, FixListItem } from "@/src/api/generated/model";
import { formatCompactNumber, formatUsd } from "@/src/lib/format";
import { countByBucket, FIX_BUCKETS, type FixBucket } from "./fix-views";

// The first rail of the Fixes surface: the lifecycle buckets with live counts (over the active
// project's fixes), then Archived as a separate destination, and the project's Conductor spend at the
// base. The surface owns the selection and supplies the spend rollup.
export function FixesViewsRail({
  items,
  activeBucket,
  onSelectBucket,
  archivedActive,
  onSelectArchived,
  spend,
}: {
  items: FixListItem[];
  activeBucket: FixBucket | null;
  onSelectBucket: (bucket: FixBucket | null) => void;
  archivedActive: boolean;
  onSelectArchived: () => void;
  spend: FixCostRollup | null;
}) {
  const translate = useTranslations("fixes");
  const counts = countByBucket(items);

  return (
    <aside className="h-full overflow-y-auto bg-card/50 p-3 text-xs">
      <div className="font-medium uppercase text-muted-foreground">{translate("views.title")}</div>
      <div className="mt-1.5 flex flex-col gap-0.5">
        <RailButton
          label={translate("views.all")}
          count={items.length}
          active={activeBucket === null && !archivedActive}
          onClick={() => onSelectBucket(null)}
        />
        {FIX_BUCKETS.map((bucket) => (
          <RailButton
            key={bucket}
            label={translate(`views.${bucket}`)}
            count={counts[bucket]}
            active={activeBucket === bucket && !archivedActive}
            onClick={() => onSelectBucket(bucket)}
          />
        ))}
      </div>

      <div className="mt-4 flex flex-col gap-0.5">
        <RailButton
          label={translate("views.archived")}
          active={archivedActive}
          onClick={onSelectArchived}
        />
      </div>

      {spend && spend.runCount > 0 ? <SpendSummary spend={spend} /> : null}
    </aside>
  );
}

// The project's Conductor spend over the default window: the total, the priced-run count, and a
// per-model breakdown. A bring-your-own model contributes tokens but no dollar cost (shown as a dash).
function SpendSummary({ spend }: { spend: FixCostRollup }) {
  const translate = useTranslations("fixes.spend");
  return (
    <div className="mt-6 border-t border-border pt-3">
      <div className="font-medium uppercase text-muted-foreground">{translate("title")}</div>
      <div className="mt-1.5 text-sm font-semibold text-foreground tabular-nums">
        {formatUsd(spend.totalUsd)}
      </div>
      <div className="text-muted-foreground">{translate("runs", { count: spend.runCount })}</div>
      <ul className="mt-2 flex flex-col gap-1">
        {spend.byModel.map((model) => (
          <li key={model.model} className="flex items-center justify-between gap-2">
            <span className="truncate text-muted-foreground" title={model.model}>
              {model.model}
            </span>
            <span className="shrink-0 tabular-nums text-foreground">
              {model.costUsd !== null
                ? formatUsd(model.costUsd)
                : translate("unpriced", {
                    tokens: formatCompactNumber(model.inputTokens + model.outputTokens),
                  })}
            </span>
          </li>
        ))}
      </ul>
    </div>
  );
}

function RailButton({
  label,
  count,
  active,
  onClick,
}: {
  label: string;
  count?: number;
  active: boolean;
  onClick: () => void;
}) {
  return (
    <button
      type="button"
      aria-pressed={active}
      onClick={onClick}
      className={`flex items-center justify-between rounded px-2 py-1.5 text-left ${
        active ? "bg-secondary text-foreground" : "text-muted-foreground hover:bg-secondary/50"
      }`}
    >
      <span>{label}</span>
      {count !== undefined ? <span className="tabular-nums">{count}</span> : null}
    </button>
  );
}
