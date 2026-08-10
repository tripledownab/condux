// The issue orderings offered by the list rail. The value strings are the wire contract: the
// control-plane's listIssues endpoint whitelists exactly these as its `sort` param, so the surface
// passes the selected value straight through (filtering + ordering + paging all happen server-side).

export enum IssueSort {
  LastSeen = "lastSeen",
  FirstSeen = "firstSeen",
  Events = "events",
  Severity = "severity",
}

export const ISSUE_SORTS: IssueSort[] = [
  IssueSort.LastSeen,
  IssueSort.FirstSeen,
  IssueSort.Events,
  IssueSort.Severity,
];
