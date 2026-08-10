-- Org-level notification channels (#129/#130, ADR-0024): where the platform delivers operational
-- notices to an org. Today's use is a **Conductor pause** notice — in auto-fix mode, when a new error
-- would have been auto-fixed but the org's AI-fix cost cap (#120) or monthly allowance (#100) is
-- reached, the org is told once (per reason, throttled) instead of silently dropping fixes. A
-- first-class org concern, deliberately separate from the project + rule scoped alert_channels (#59).
-- Idempotent.

CREATE TABLE IF NOT EXISTS org_notification_channels (
    id         UUID        PRIMARY KEY,
    org_id     BIGINT      NOT NULL REFERENCES orgs (id) ON DELETE CASCADE,
    channel    SMALLINT    NOT NULL,   -- NotificationChannel: 1 email, 2 slack, 3 webhook
    target     TEXT        NOT NULL,   -- the email address or Slack/webhook URL
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS org_notification_channels_org_idx ON org_notification_channels (org_id);

-- Throttle state so a persistent pause condition notifies at most once per org per reason per window
-- (a day by default) instead of on every dropped auto-fix. One row per (org, reason); the notify path
-- upserts it and only sends when the previous send is older than the window.
CREATE TABLE IF NOT EXISTS org_pause_notifications (
    org_id      BIGINT      NOT NULL REFERENCES orgs (id) ON DELETE CASCADE,
    reason      SMALLINT    NOT NULL,   -- ConductorPauseReason: 1 cost cap, 2 allowance exhausted
    notified_at TIMESTAMPTZ NOT NULL,
    PRIMARY KEY (org_id, reason)
);
