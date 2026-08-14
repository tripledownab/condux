"use client";

import { useTranslations } from "next-intl";
import { type ReactNode, useState } from "react";
import {
  useGetIssue,
  useListCodeMappings,
  useListMembers,
  useListRepos,
  useMe,
} from "@/src/api/generated/condux";
import type { IssueSummary } from "@/src/api/generated/model";
import { Notice } from "@/src/components/notice";
import { Button } from "@/src/components/ui/button";
import { formatRelativeTime } from "@/src/lib/time";
import { ProjectStatus, useCurrentProject } from "./current-project";
import { EventChips, EventEvidence } from "./event-detail";
import {
  hasEvidence,
  type ParsedEvent,
  parseEventPayload,
  primaryException,
} from "./event-payload";
import { EventsTable } from "./events-table";
import { FacetsPanel } from "./facets-panel";
import { FixPanel } from "./fix-panel";
import { frameGithubUrl } from "./github-links";
import { IssueActions } from "./issue-actions";
import { IssueChart } from "./issue-chart";
import { usersAffected } from "./issue-facets";
import { statusKey } from "./issue-format";
import { IssueNotes } from "./issue-notes";
import { LevelBadge } from "./level-badge";
import { PayloadViewer } from "./payload-viewer";

// A single grouped issue plus its most-recent sampled events. The project is resolved the same way as
// the list (first membership); an issue that cannot be reached there reads as not-found.
export function IssueDetail({ issueId }: { issueId: string }) {
  const translate = useTranslations("issues.detail");
  // The event whose raw payload is open in the slide-up viewer, if any, with the terms to highlight.
  const [rawPayload, setRawPayload] = useState<{ payload: string; highlights: string[] } | null>(
    null,
  );
  const current = useCurrentProject();
  const projectId = current.status === ProjectStatus.Ready ? current.project.id : 0;
  const enabled = current.status === ProjectStatus.Ready && issueId !== "";
  const query = useGetIssue(projectId, issueId, { query: { enabled } });
  // Repo links + code mappings turn in-app frames into GitHub deep links; best-effort, resolved
  // through the first linked repo's mappings (mappings are keyed per repo link).
  const repos = useListRepos(projectId, { query: { enabled } });
  const orgId = current.status === ProjectStatus.Ready ? current.org.id : 0;
  const members = useListMembers(orgId, { query: { enabled } });
  // The current user + whether they can moderate (admin/owner), so a note's Delete shows for its author
  // or an admin. The backend enforces the same rule; this only gates the button.
  const me = useMe();
  const repoList = repos.data?.data ?? [];
  const firstRepoId = repoList[0]?.id ?? "";
  const codeMappings = useListCodeMappings(projectId, firstRepoId, {
    query: { enabled: enabled && firstRepoId !== "" },
  });
  const frameUrl = (frame: Parameters<typeof frameGithubUrl>[0]) =>
    frameGithubUrl(frame, repoList, codeMappings.data?.data ?? []);

  if (current.status === ProjectStatus.Loading) {
    return (
      <Shell>
        <Notice>{translate("loading")}</Notice>
      </Shell>
    );
  }
  if (current.status === ProjectStatus.Error) {
    return (
      <Shell>
        <Notice>{translate("error")}</Notice>
      </Shell>
    );
  }
  if (current.status !== ProjectStatus.Ready || issueId === "") {
    return (
      <Shell>
        <Notice>{translate("notFound")}</Notice>
      </Shell>
    );
  }
  if (query.isPending) {
    return (
      <Shell>
        <Notice>{translate("loading")}</Notice>
      </Shell>
    );
  }
  // getIssue returns 404 (typed as void) for a missing issue or one outside the project.
  if (query.isError || !query.data?.data) {
    return (
      <Shell>
        <Notice>{translate("notFound")}</Notice>
      </Shell>
    );
  }

  const detail = query.data.data;
  // Parse every sampled payload once; the cards, the facets panel, and the header badge all read it.
  const parsedByEventId = new Map<string, ParsedEvent | null>(
    detail.events.map((event) => [event.eventId, parseEventPayload(event.payload)]),
  );
  const parsedEvents = [...parsedByEventId.values()].filter(
    (event): event is ParsedEvent => event !== null,
  );
  const latest = parsedEvents[0];

  // Layout 30's issue page: header, the latest event's chips, the chart, then the code path and
  // breadcrumbs read down the main column while the fixed right rail (actions first, then impact,
  // the Conductor and facets) keeps its own scroll. Older sampled events follow as cards; any
  // event's raw payload opens in the slide-up viewer covering the whole view.
  const latestStored = detail.events[0];
  const currentUserId = me.data?.data.id ?? null;
  const myRole = (members.data?.data ?? []).find((member) => member.userId === currentUserId)?.role;
  const canModerate = myRole === "admin" || myRole === "owner";
  return (
    <div className="relative flex h-full">
      <div className="min-w-0 flex-1 overflow-y-auto p-6">
        <IssueHeader issue={detail.issue} />
        <div className="mt-4">
          {latest !== undefined ? (
            <EventChips event={latest} leading={<IssueLabels issue={detail.issue} />} />
          ) : (
            <div className="flex flex-wrap items-center gap-1.5">
              <IssueLabels issue={detail.issue} />
            </div>
          )}
        </div>
        <IssueChart projectId={current.project.id} issueId={issueId} />
        {latest !== undefined && hasEvidence(latest) ? (
          <div className="mt-6">
            <EventEvidence event={latest} frameUrl={frameUrl} />
          </div>
        ) : null}
        {latestStored !== undefined ? (
          <Button
            type="button"
            variant="secondary"
            size="sm"
            onClick={() =>
              setRawPayload({
                payload: latestStored.payload,
                highlights: payloadHighlights(latest ?? null),
              })
            }
            className="mt-3"
          >
            {translate("rawPayload")}
          </Button>
        ) : null}
        <EventsTable
          projectId={current.project.id}
          issueId={issueId}
          onShowRaw={(event) =>
            setRawPayload({
              payload: event.payload,
              highlights: payloadHighlights(parseEventPayload(event.payload)),
            })
          }
        />
      </div>
      <aside className="flex w-80 shrink-0 flex-col gap-5 overflow-y-auto border-l border-border bg-card/30 p-4">
        <IssueMetaRail
          issue={detail.issue}
          users={usersAffected(parsedEvents)}
          assigneeEmail={
            members.data?.data?.find((member) => member.userId === detail.issue.assigneeUserId)
              ?.email ?? null
          }
        />
        <IssueActions
          projectId={current.project.id}
          issueId={issueId}
          status={detail.issue.status}
          orgId={orgId}
          assigneeUserId={detail.issue.assigneeUserId ?? null}
        />
        <FixPanel
          projectId={current.project.id}
          issueId={issueId}
          orgId={orgId}
          lastSeen={detail.issue.lastSeen}
        />
        <IssueNotes
          projectId={current.project.id}
          issueId={issueId}
          currentUserId={currentUserId}
          canModerate={canModerate}
        />
        <FacetsPanel events={parsedEvents} />
      </aside>
      {rawPayload !== null ? (
        <PayloadViewer
          payload={rawPayload.payload}
          highlights={rawPayload.highlights}
          onClose={() => setRawPayload(null)}
        />
      ) : null}
    </div>
  );
}

function Shell({ children }: { children: ReactNode }) {
  return <div className="h-full overflow-y-auto p-6">{children}</div>;
}

function IssueHeader({ issue }: { issue: IssueSummary }) {
  return (
    <section>
      <h1 className="font-heading text-xl font-semibold text-foreground">{issue.title}</h1>
      <p className="mt-1 truncate font-mono text-sm text-muted-foreground">{issue.culprit}</p>
    </section>
  );
}

// Severity and status lead the metadata row under the title; the chips follow behind the separator.
function IssueLabels({ issue }: { issue: IssueSummary }) {
  const translateStatus = useTranslations("issues.status");
  return (
    <>
      <LevelBadge level={issue.level} />
      <span className="text-xs uppercase text-muted-foreground">
        {translateStatus(statusKey(issue.status))}
      </span>
    </>
  );
}

// The right rail's impact block (layout 30): events, distinct users (from the sampled events, so a
// floor not a total), first and last seen.
function IssueMetaRail({
  issue,
  users,
  assigneeEmail,
}: {
  issue: IssueSummary;
  users: number;
  assigneeEmail: string | null;
}) {
  const translate = useTranslations("issues.detail");
  // Bare on the rail's own tint; the rail's left border is chrome enough.
  return (
    <section>
      <dl className="grid grid-cols-2 gap-y-2 text-sm">
        <dt className="text-xs uppercase text-muted-foreground">{translate("events")}</dt>
        <dd className="text-right font-medium text-foreground">{issue.eventCount}</dd>
        <dt className="text-xs uppercase text-muted-foreground">{translate("users")}</dt>
        <dd className="text-right font-medium text-foreground">{users}</dd>
        <dt className="text-xs uppercase text-muted-foreground">{translate("firstSeen")}</dt>
        <dd className="text-right text-foreground">{formatRelativeTime(issue.firstSeen)}</dd>
        {issue.firstRelease ? (
          <>
            <dt className="text-xs uppercase text-muted-foreground">{translate("firstRelease")}</dt>
            <dd
              className="min-w-0 truncate text-right font-mono text-foreground"
              title={issue.firstRelease}
            >
              {issue.firstRelease}
            </dd>
          </>
        ) : null}
        <dt className="text-xs uppercase text-muted-foreground">{translate("lastSeen")}</dt>
        <dd className="text-right text-foreground">{formatRelativeTime(issue.lastSeen)}</dd>
        <dt className="text-xs uppercase text-muted-foreground">{translate("assignee")}</dt>
        <dd className="min-w-0 truncate text-right text-foreground">
          {assigneeEmail ?? translate("unassigned")}
        </dd>
      </dl>
    </section>
  );
}

// The payload lines worth highlighting in the raw viewer: the exception's message and every in-app
// frame's culprit source line.
function payloadHighlights(parsed: ParsedEvent | null): string[] {
  if (parsed === null) {
    return [];
  }
  const primary = primaryException(parsed);
  const terms: string[] = [];
  if (primary?.value) {
    terms.push(primary.value);
  }
  for (const frame of primary?.frames ?? []) {
    if (frame.inApp && frame.contextLine && frame.contextLine.trim().length > 0) {
      terms.push(frame.contextLine.trim());
    }
  }
  return terms;
}
