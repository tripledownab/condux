"use client";

import { useFormatter, useTranslations } from "next-intl";
import { useState } from "react";
import { useGetIssueStats } from "@/src/api/generated/condux";
import type { HistogramBucket } from "@/src/api/generated/model";

// The selectable windows, mirroring the API's accepted values (hourly buckets up to 7d, daily for 30d).
const CHART_RANGES = [
  { hours: 24, labelKey: "range24h" },
  { hours: 168, labelKey: "range7d" },
  { hours: 720, labelKey: "range30d" },
] as const;

const HOUR_SECONDS = 3_600;

// The Sentry-style issue timeline: exact event counts per bucket from the unsampled rollup, as a bar
// chart with a range toggle. Bars are plain divs (no chart dependency); the count scale sits beside the
// plot, date labels below it, and each bar carries its bucket time + count as a tooltip.
export function IssueChart({ projectId, issueId }: { projectId: number; issueId: string }) {
  const translate = useTranslations("issues.chart");
  const format = useFormatter();
  const [hours, setHours] = useState<number>(CHART_RANGES[0].hours);
  const stats = useGetIssueStats(projectId, issueId, { hours });

  const buckets = stats.data?.status === 200 ? stats.data.data.buckets : [];
  const bucketSeconds = stats.data?.status === 200 ? stats.data.data.bucketSeconds : HOUR_SECONDS;
  const total = buckets.reduce((sum, bucket) => sum + bucket.count, 0);
  const max = buckets.reduce((peak, bucket) => Math.max(peak, bucket.count), 0);

  // Hourly buckets label with the time, daily ones with the date alone.
  const bucketLabel = (bucket: HistogramBucket) =>
    bucketSeconds > HOUR_SECONDS
      ? format.dateTime(new Date(bucket.ts * 1000), { month: "short", day: "numeric" })
      : format.dateTime(new Date(bucket.ts * 1000), {
          month: "short",
          day: "numeric",
          hour: "2-digit",
          minute: "2-digit",
        });

  return (
    <section className="mt-6 rounded-lg border border-border bg-card p-4">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div className="flex items-baseline gap-3">
          <h2 className="font-heading text-sm font-semibold uppercase text-muted-foreground">
            {translate("title")}
          </h2>
          {stats.isSuccess ? (
            <span className="text-sm text-foreground">{translate("total", { count: total })}</span>
          ) : null}
        </div>
        <div className="flex gap-1">
          {CHART_RANGES.map((range) => (
            <button
              key={range.hours}
              type="button"
              onClick={() => setHours(range.hours)}
              className={`rounded px-2 py-1 text-xs transition-colors ${
                hours === range.hours
                  ? "bg-primary text-primary-foreground"
                  : "text-muted-foreground hover:bg-secondary hover:text-foreground"
              }`}
            >
              {translate(range.labelKey)}
            </button>
          ))}
        </div>
      </div>

      <div className="mt-3">
        {stats.isPending ? (
          <p className="py-8 text-center text-sm text-muted-foreground">{translate("loading")}</p>
        ) : stats.isError ? (
          <p className="py-8 text-center text-sm text-muted-foreground">{translate("error")}</p>
        ) : (
          <div>
            <div className="flex gap-2">
              {/* The count scale: the window's peak up top, zero at the baseline. */}
              <div className="flex w-8 flex-col justify-between text-right text-[10px] tabular-nums text-muted-foreground">
                <span>{max}</span>
                <span>0</span>
              </div>
              <div
                className="relative flex h-24 flex-1 items-end gap-px"
                role="img"
                aria-label={translate("title")}
              >
                {/* Vertical grid lines at the quarter marks, behind the bars. */}
                {["25%", "50%", "75%"].map((left) => (
                  <span
                    key={left}
                    aria-hidden="true"
                    className="pointer-events-none absolute inset-y-0 w-px bg-border/60"
                    style={{ left }}
                  />
                ))}
                {buckets.map((bucket) => (
                  // A CSS-only tooltip (shown instantly on hover, unlike the sluggish native title):
                  // the label floats above the hovered column and the bar highlights.
                  <div
                    key={bucket.ts}
                    title={translate("bucketTooltip", {
                      time: bucketLabel(bucket),
                      count: bucket.count,
                    })}
                    className="group relative flex flex-1 flex-col justify-end self-stretch hover:bg-secondary/40"
                  >
                    <span className="pointer-events-none absolute bottom-full left-1/2 z-10 mb-1 hidden -translate-x-1/2 whitespace-nowrap rounded border border-border bg-popover px-2 py-1 text-[10px] text-popover-foreground shadow-sm group-hover:block">
                      {translate("bucketTooltip", {
                        time: bucketLabel(bucket),
                        count: bucket.count,
                      })}
                    </span>
                    <div
                      className={
                        bucket.count > 0
                          ? "rounded-sm bg-primary group-hover:opacity-80"
                          : "rounded-sm bg-secondary"
                      }
                      style={{
                        height:
                          bucket.count > 0 && max > 0 ? `${(bucket.count / max) * 100}%` : "2px",
                      }}
                    />
                  </div>
                ))}
              </div>
            </div>
            {buckets.length > 0 ? (
              <div className="mt-1 flex justify-between pl-10 text-[10px] text-muted-foreground">
                <span>{bucketLabel(buckets[0])}</span>
                <span>{bucketLabel(buckets[Math.floor(buckets.length / 2)])}</span>
                <span>{bucketLabel(buckets[buckets.length - 1])}</span>
              </div>
            ) : null}
          </div>
        )}
      </div>
    </section>
  );
}
