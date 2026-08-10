// The single source of truth for the issue keyboard shortcuts: which key does what. The help overlay
// renders this list, and the handlers (issue-list-rail j/k, issue-actions e/i/a) bind the same keys.
// `labelKey` is the i18n key under the `issues.shortcuts` namespace.
export const ISSUE_SHORTCUTS: ReadonlyArray<{ keys: readonly string[]; labelKey: string }> = [
  { keys: ["j"], labelKey: "nextIssue" },
  { keys: ["k"], labelKey: "prevIssue" },
  { keys: ["e"], labelKey: "resolve" },
  { keys: ["i"], labelKey: "ignore" },
  { keys: ["a"], labelKey: "assign" },
  { keys: ["?"], labelKey: "help" },
];
