// Notification channel transport enum + option lists, shared by the project alert-rule channels (#59) and
// the org notification channels (#129). Mirrors backend Condux.Core.Alerting.NotificationChannel. Label
// keys resolve under the shared "settings.channels" namespace. Generic helpers live in @/src/lib.

export enum NotificationChannel {
  Email = 1,
  Slack = 2,
  Webhook = 3,
  Discord = 4,
}

// Channels offered when adding a delivery target, with a label key under "settings.channels".
export const CHANNEL_OPTIONS: ReadonlyArray<{ value: NotificationChannel; key: string }> = [
  { value: NotificationChannel.Email, key: "email" },
  { value: NotificationChannel.Slack, key: "slack" },
  { value: NotificationChannel.Discord, key: "discord" },
  { value: NotificationChannel.Webhook, key: "webhook" },
];

const CHANNEL_KEYS: Record<number, string> = {
  [NotificationChannel.Email]: "email",
  [NotificationChannel.Slack]: "slack",
  [NotificationChannel.Discord]: "discord",
  [NotificationChannel.Webhook]: "webhook",
};

export function channelKey(channel: number): string {
  return CHANNEL_KEYS[channel] ?? "unknown";
}

// Which markdown each transport renders, surfaced as a note in the message-template editor. Slack uses its
// own mrkdwn (single-asterisk bold, <url|label> links); Discord uses **bold** + [label](url) masked links;
// email and webhook do not render markdown. labelKey resolves under "settings.channels"; the example is
// literal syntax (not translated). Verified against the Slack + Discord formatting docs (2026-07).
export const CHANNEL_MARKDOWN: Record<number, { labelKey: string; example?: string }> = {
  [NotificationChannel.Slack]: {
    labelKey: "markdownSlack",
    example: "*bold*  _italic_  ~strike~  `code`  <url|label>",
  },
  [NotificationChannel.Discord]: {
    labelKey: "markdownDiscord",
    example: "**bold**  *italic*  ~~strike~~  `code`  [label](url)",
  },
  [NotificationChannel.Email]: { labelKey: "markdownEmail" },
  [NotificationChannel.Webhook]: { labelKey: "markdownWebhook" },
};
