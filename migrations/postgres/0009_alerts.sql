-- Alerting (P3, #54): rules and their notification channels. A rule belongs to a project and fires on
-- new and/or regressed issues at or above a minimum severity; each rule fans out to one or more channels
-- (email / Slack / webhook). Idempotent, like the other migrations.

CREATE TABLE IF NOT EXISTS alert_rules (
    id             UUID PRIMARY KEY,
    project_id     BIGINT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    name           TEXT NOT NULL,
    on_new_issue   BOOLEAN NOT NULL DEFAULT TRUE,
    on_regression  BOOLEAN NOT NULL DEFAULT TRUE,
    -- Condux.Core.Events.Level: 0=Unspecified,1=Debug,2=Info,3=Warning,4=Error,5=Fatal. Default Error.
    min_level      SMALLINT NOT NULL DEFAULT 4,
    enabled        BOOLEAN NOT NULL DEFAULT TRUE,
    created_at     TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS alert_rules_project_idx ON alert_rules (project_id);

CREATE TABLE IF NOT EXISTS alert_channels (
    id          UUID PRIMARY KEY,
    rule_id     UUID NOT NULL REFERENCES alert_rules (id) ON DELETE CASCADE,
    -- Condux.Core.Alerting.NotificationChannel: 1=Email, 2=Slack, 3=Webhook.
    channel     SMALLINT NOT NULL,
    -- The email address, Slack incoming-webhook URL, or webhook URL the alert is delivered to.
    target      TEXT NOT NULL,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS alert_channels_rule_idx ON alert_channels (rule_id);
