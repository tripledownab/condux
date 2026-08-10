"use client";

import { useQueryClient } from "@tanstack/react-query";
import Link from "next/link";
import { useTranslations } from "next-intl";
import { type ReactNode, useEffect } from "react";
import {
  getListProjectFixesQueryKey,
  getUnviewedFixCountQueryKey,
  useArchiveFix,
  useGetFix,
  useMarkFixViewed,
} from "@/src/api/generated/condux";
import type { FixAuditEntry } from "@/src/api/generated/model";
import { Markdown } from "@/src/components/markdown";
import { Notice } from "@/src/components/notice";
import { Button } from "@/src/components/ui/button";
import { ProjectStatus, useCurrentProject } from "@/src/issues/current-project";
import { FixStatus, fixStatusMeta, isFixInFlight, verifyStatusMeta } from "@/src/issues/fix-format";
import { formatCompactNumber, formatUsd } from "@/src/lib/format";
import { formatRelativeTime, formatTimestamp } from "@/src/lib/time";
import { issueDetailPath } from "@/src/routes";

// A single fix run: the layout-30 detail (title + hairline meta row, main column, fixed right rail),
// populated with fix content — summary, the draft PR, provider/model + token usage, the audit
// timeline, and a link back to the originating issue. Opening it marks it viewed (clearing it from
// the nav badge); the run polls while it is still in flight.
export function FixDetail({ fixId }: { fixId: string }) {
  const translate = useTranslations("fixes.detail");
  const translateStatus = useTranslations("issues.fix.status");
  const translateVerify = useTranslations("issues.fix.verify");
  const queryClient = useQueryClient();
  const current = useCurrentProject();
  const ready = current.status === ProjectStatus.Ready;
  const projectId = ready ? current.project.id : 0;
  const enabled = ready && fixId !== "";

  const query = useGetFix(projectId, fixId, {
    query: {
      enabled,
      refetchInterval: (q) =>
        q.state.data?.data && isFixInFlight(q.state.data.data.status) ? 2000 : false,
    },
  });

  const markViewed = useMarkFixViewed();
  // Opening the detail marks it viewed once and refreshes the badge. Idempotent server-side, so a
  // re-fire on refetch is harmless; keyed on fixId so switching fixes re-marks. The mutation and query
  // client are stable references, so they stay out of the deps to keep this to one fire per fix.
  // biome-ignore lint/correctness/useExhaustiveDependencies: mark viewed once per fix; markViewed/queryClient are stable
  useEffect(() => {
    if (!enabled) {
      return;
    }
    markViewed.mutate(
      { projectId, fixId },
      {
        onSuccess: () =>
          queryClient.invalidateQueries({ queryKey: getUnviewedFixCountQueryKey(projectId) }),
      },
    );
  }, [enabled, projectId, fixId]);

  const archive = useArchiveFix({
    mutation: {
      onSuccess: () => {
        queryClient.invalidateQueries({ queryKey: getListProjectFixesQueryKey(projectId) });
        query.refetch();
      },
    },
  });

  if (current.status === ProjectStatus.Loading || query.isPending) {
    return (
      <Shell>
        <Notice>{translate("loading")}</Notice>
      </Shell>
    );
  }
  if (!ready || fixId === "" || query.isError || !query.data?.data) {
    return (
      <Shell>
        <Notice>{translate("notFound")}</Notice>
      </Shell>
    );
  }

  const fix = query.data.data;
  const statusMeta = fixStatusMeta(fix.status);
  const verifyMeta = verifyStatusMeta(fix.verifyStatus);
  const hasPr = fix.status === FixStatus.Succeeded && fix.prUrl !== "";
  const hasTokens = fix.inputTokens > 0 || fix.outputTokens > 0;

  return (
    <div className="flex h-full">
      <div className="min-w-0 flex-1 overflow-y-auto p-6">
        <h1 className="font-heading text-xl font-semibold text-foreground">{fix.issueTitle}</h1>
        <div className="mt-2 flex flex-wrap items-center gap-x-3 gap-y-1 text-xs">
          {/* Once merged the verification state is the true status; "Draft PR ready" would be stale,
              so the verify badge replaces the run status rather than joining it. */}
          {verifyMeta ? (
            <span className={`uppercase ${verifyMeta.className}`}>
              {translateVerify(verifyMeta.key)}
            </span>
          ) : (
            <span className={`uppercase ${statusMeta.className}`}>
              {translateStatus(statusMeta.key)}
            </span>
          )}
          <span className="text-muted-foreground">{fix.repoFullName}</span>
          <span className="text-muted-foreground">
            {translate("requested")} {formatRelativeTime(fix.createdAt)}
          </span>
        </div>

        {/* The run's metadata card leads; the summary reads below it. */}
        <section className="mt-4 rounded-lg border border-border bg-card p-4 text-sm">
          <div>
            {translate("branch")}: <code className="text-foreground">{fix.branch}</code>
          </div>
          <div className="mt-2 flex flex-wrap gap-x-4 gap-y-1 text-xs text-muted-foreground">
            <span>
              {translate("provider")}: <span className="text-foreground">{fix.provider}</span>
            </span>
            <span>
              {translate("model")}: <span className="text-foreground">{fix.model}</span>
            </span>
            {hasTokens ? (
              <span>
                {translate("tokens")}:{" "}
                <span className="text-foreground">
                  {formatCompactNumber(fix.inputTokens)} / {formatCompactNumber(fix.outputTokens)}
                </span>
              </span>
            ) : null}
            {fix.costUsd !== null ? (
              <span>
                {translate("cost")}:{" "}
                <span className="text-foreground">{formatUsd(fix.costUsd)}</span>
              </span>
            ) : null}
            <span>
              {translate("created")}:{" "}
              <span className="text-foreground">{formatTimestamp(fix.createdAt)}</span>
            </span>
            {fix.mergedAt ? (
              <span>
                {translate("merged")}:{" "}
                <span className="text-foreground">{formatTimestamp(fix.mergedAt)}</span>
              </span>
            ) : null}
            {fix.verifiedAt ? (
              <span>
                {translate("verified")}:{" "}
                <span className="text-foreground">{formatTimestamp(fix.verifiedAt)}</span>
              </span>
            ) : null}
          </div>
        </section>

        {fix.summary ? (
          <div className="mt-4">
            <Markdown>{fix.summary}</Markdown>
          </div>
        ) : null}

        <AuditTimeline audit={fix.audit} />
      </div>

      {/* The rail is actions only; run metadata lives in the card in the main column. */}
      <aside className="flex w-72 shrink-0 flex-col gap-2 overflow-y-auto border-l border-border bg-card/30 p-4 text-sm">
        <Button asChild variant="secondary">
          <Link href={issueDetailPath(fix.issuePublicId)}>{translate("viewIssue")}</Link>
        </Button>
        {hasPr ? (
          <Button asChild variant="secondary">
            <a href={fix.prUrl} target="_blank" rel="noopener noreferrer">
              {translate("viewPr")}
            </a>
          </Button>
        ) : null}
        <Button
          variant="secondary"
          onClick={() => archive.mutate({ projectId, fixId, data: { archived: !fix.archived } })}
          disabled={archive.isPending}
        >
          {fix.archived ? translate("unarchive") : translate("archive")}
        </Button>
      </aside>
    </div>
  );
}

function AuditTimeline({ audit }: { audit: FixAuditEntry[] }) {
  const translate = useTranslations("fixes.detail");
  if (audit.length === 0) {
    return null;
  }
  // The trail is append-only and ordered, so a row's position is part of its identity; the key is
  // stamped here rather than from the map callback (an index key in JSX is a lint smell).
  const rows = audit.map((entry, index) => ({ entry, key: `${index}-${entry.event}` }));
  return (
    <section className="mt-5">
      <h2 className="text-xs font-medium uppercase text-muted-foreground">
        {translate("timeline")}
      </h2>
      <ol className="mt-2 flex flex-col gap-2">
        {rows.map(({ entry, key }) => (
          <li key={key} className="flex items-baseline gap-2 text-xs">
            <span className="text-foreground">{translate(`audit.${entry.event}`)}</span>
            <span className="text-muted-foreground">{entry.actor}</span>
            <span className="ml-auto text-muted-foreground">
              {formatRelativeTime(entry.createdAt)}
            </span>
          </li>
        ))}
      </ol>
    </section>
  );
}

function Shell({ children }: { children: ReactNode }) {
  return <div className="h-full overflow-y-auto p-6">{children}</div>;
}
