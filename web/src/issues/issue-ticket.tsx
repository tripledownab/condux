"use client";

import { useQueryClient } from "@tanstack/react-query";
import { useTranslations } from "next-intl";
import { useState } from "react";
import {
  getGetIssueTicketQueryKey,
  useCreateIssueTicket,
  useGetIssueTicket,
  useGetLinearProject,
  useRetryIssueTicket,
  useUnlinkIssueTicket,
} from "@/src/api/generated/condux";
import type { IssueTicketResponse } from "@/src/api/generated/model";
import { PRIMARY_BUTTON_CLASS, SECONDARY_BUTTON_CLASS } from "@/src/components/form";
import { isInFlight, TicketState, toFailureKey } from "@/src/linear/ticket-state";

// How often the rail asks while the worker has the ticket. The project's event stream usually answers
// sooner; this is what keeps a missed nudge from leaving "Creating" on screen.
const IN_FLIGHT_POLL_MS = 3000;

// The issue rail's Linear action (ADR-0053). Shown only when this project has a Linear team, since without
// one there is nowhere for a ticket to go. Creating confirms first, because it sends the issue's title to
// a third party, and then only reserves: the ticket appears once the worker has made it.
export function IssueTicket({
  projectId,
  issueId,
  issueTitle,
}: {
  projectId: number;
  issueId: string;
  issueTitle: string;
}) {
  const translate = useTranslations("issues.ticket");
  const queryClient = useQueryClient();
  const linear = useGetLinearProject(projectId);
  const status = linear.data?.status === 200 ? linear.data.data : null;
  // An existing ticket stays visible whatever happened to the team since; only a new one needs somewhere
  // to go, which is a mapped team on a connection Linear still accepts.
  const connected = status?.connected === true;
  const mapping = status && !status.broken ? (status.mapping ?? null) : null;
  const ticketQuery = useGetIssueTicket(projectId, issueId, {
    query: {
      enabled: connected,
      refetchInterval: (query) => {
        const data = query.state.data;
        return data?.status === 200 && isInFlight(data.data.state) ? IN_FLIGHT_POLL_MS : false;
      },
    },
  });
  const create = useCreateIssueTicket();
  const retry = useRetryIssueTicket();
  const unlink = useUnlinkIssueTicket();
  const [confirming, setConfirming] = useState(false);
  const refresh = () =>
    queryClient.invalidateQueries({ queryKey: getGetIssueTicketQueryKey(projectId, issueId) });

  if (!connected || ticketQuery.isPending) {
    return null;
  }
  const ticket = ticketQuery.data?.status === 200 ? ticketQuery.data.data : null;
  if (ticket === null && mapping === null) {
    return null;
  }

  return (
    <section className="flex flex-col gap-2">
      <h3 className="text-xs font-medium uppercase tracking-wide text-muted-foreground">
        {translate("heading")}
      </h3>
      {/* The rule, not a verdict on this issue: an issue's level can change after its first event, and the
          daily limit can be what stopped it, so a claim about why this one has none could be wrong. */}
      {ticket === null && mapping?.autoCreate ? (
        <p className="text-xs text-muted-foreground">{translate("notAutomatic")}</p>
      ) : null}
      {ticket === null && mapping !== null ? (
        confirming ? (
          <div className="flex flex-col gap-2 rounded-md border border-border bg-background p-3">
            <p className="text-sm text-muted-foreground">
              {translate("confirm", { team: mapping.teamName, title: issueTitle })}
            </p>
            <div className="flex flex-wrap gap-2">
              <button
                type="button"
                disabled={create.isPending}
                className={PRIMARY_BUTTON_CLASS}
                onClick={() =>
                  create.mutate(
                    { projectId, issueId },
                    {
                      onSuccess: () => {
                        setConfirming(false);
                        refresh();
                      },
                    },
                  )
                }
              >
                {translate("create")}
              </button>
              <button
                type="button"
                onClick={() => setConfirming(false)}
                className={SECONDARY_BUTTON_CLASS}
              >
                {translate("cancel")}
              </button>
            </div>
            {create.isError ? (
              <p className="text-sm text-error">{translate("createFailed")}</p>
            ) : null}
          </div>
        ) : (
          <button
            type="button"
            onClick={() => setConfirming(true)}
            className={SECONDARY_BUTTON_CLASS}
          >
            {translate("createLinear")}
          </button>
        )
      ) : ticket !== null ? (
        <TicketStatus
          ticket={ticket}
          busy={retry.isPending || unlink.isPending}
          onRetry={() => retry.mutate({ projectId, issueId }, { onSuccess: refresh })}
          onUnlink={() => unlink.mutate({ projectId, issueId }, { onSuccess: refresh })}
        />
      ) : null}
    </section>
  );
}

// One line per state. Created links out; failed and uncertain both offer a retry, and uncertain says to
// look in Linear first, since the ticket may already be there.
function TicketStatus({
  ticket,
  busy,
  onRetry,
  onUnlink,
}: {
  ticket: IssueTicketResponse;
  busy: boolean;
  onRetry: () => void;
  onUnlink: () => void;
}) {
  const translate = useTranslations("issues.ticket");
  if (isInFlight(ticket.state)) {
    return <p className="text-sm text-muted-foreground">{translate("creating")}</p>;
  }
  if (ticket.state === TicketState.Created && ticket.url) {
    return (
      <div className="flex flex-wrap items-center gap-2 text-sm">
        <a
          href={ticket.url}
          target="_blank"
          rel="noopener noreferrer"
          className="font-mono underline"
        >
          {ticket.key}
        </a>
        <button
          type="button"
          disabled={busy}
          onClick={onUnlink}
          className="text-xs text-muted-foreground underline"
        >
          {translate("unlink")}
        </button>
        {ticket.automatic ? (
          <span className="text-xs text-muted-foreground">{translate("openedAutomatically")}</span>
        ) : null}
      </div>
    );
  }
  const message =
    ticket.state === TicketState.Uncertain
      ? translate("uncertain")
      : translate(toFailureKey(ticket.failure));
  return (
    <div className="flex flex-col items-start gap-2">
      <p className="text-sm text-error">{message}</p>
      <div className="flex flex-wrap gap-2">
        <button type="button" disabled={busy} onClick={onRetry} className={SECONDARY_BUTTON_CLASS}>
          {translate("retry")}
        </button>
        <button type="button" disabled={busy} onClick={onUnlink} className={SECONDARY_BUTTON_CLASS}>
          {translate("unlink")}
        </button>
      </div>
    </div>
  );
}
