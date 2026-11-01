// The ticket values the API sends as integers (IssueTicketResponse.state and .failure), named here so no
// component compares against a bare number. They are the stored values of Condux.Core.Tickets, which
// never change once written, so a new value is an addition on both sides.

export enum TicketState {
  Pending = 1,
  Claimed = 2,
  Created = 3,
  Failed = 4,
  Uncertain = 5,
}

export enum TicketFailure {
  NotMapped = 1,
  NotConnected = 2,
  ConnectionBroken = 3,
  TeamUnavailable = 4,
  Refused = 5,
}

// Still with the worker: the rail polls while a ticket is in one of these.
export function isInFlight(state: number): boolean {
  return state === TicketState.Pending || state === TicketState.Claimed;
}

// The message key for a failure, under issues.ticket. An unknown value means the API moved ahead of this
// build, and the generic refusal is the honest thing to show.
const FAILURE_KEYS: Record<TicketFailure, string> = {
  [TicketFailure.NotMapped]: "failureNotMapped",
  [TicketFailure.NotConnected]: "failureNotConnected",
  [TicketFailure.ConnectionBroken]: "failureConnectionBroken",
  [TicketFailure.TeamUnavailable]: "failureTeamUnavailable",
  [TicketFailure.Refused]: "failureRefused",
};

export function toFailureKey(failure: number | null | undefined): string {
  return FAILURE_KEYS[failure as TicketFailure] ?? FAILURE_KEYS[TicketFailure.Refused];
}
