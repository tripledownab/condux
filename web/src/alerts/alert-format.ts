// Alert-domain option lists. The channel transport enum + options live in @/src/channels/format (shared
// with the org notification channels). Level values are Condux.Core.Events.Level (reusing the issue level
// labels). Generic helpers live in @/src/lib.

// Minimum severities a rule can fire at (Level info/warning/error/fatal); labels come from the shared
// issue level catalog ("issues.level") via levelMeta.
export const LEVEL_OPTIONS: ReadonlyArray<number> = [2, 3, 4, 5];

// Issue events a rule can fire on (mirrors Condux.Core.Alerting.AlertEventType). Labels resolve under
// "settings.alerts.event.<key>".
export const EVENT_OPTIONS: ReadonlyArray<{ value: number; key: string }> = [
  { value: 1, key: "newIssue" },
  { value: 2, key: "regression" },
  { value: 3, key: "resolved" },
  { value: 4, key: "assigned" },
];

export function eventKey(value: number): string {
  return EVENT_OPTIONS.find((option) => option.value === value)?.key ?? "unknown";
}

// The alert message template: the built-in default + the placeholder tokens a channel may use (mirrors
// Condux.Core.Alerting.AlertTemplate). "Restore default" resets a channel's template to this string; the
// server normalizes it (or an empty template) back to null. Token labels come from "settings.channels.token".
export const ALERT_TEMPLATE_DEFAULT = "{{event}} [{{level}}] {{title}}\n{{culprit}}";

export const ALERT_TOKEN_NAMES = [
  "event",
  "level",
  "title",
  "culprit",
  "project",
  "issue",
] as const;
