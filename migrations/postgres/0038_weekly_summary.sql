-- Weekly summary emails (ADR-0031): a per-org scheduled digest emailed to every member. Three parts:
-- (1) per-org enable + schedule columns on orgs; (2) issues.resolved_at so "resolved this week" is a real
-- query (forward-looking — issues resolved before this migration have no timestamp); (3) a small send-ledger
-- so the hourly-wake worker sends each week's digest exactly once, restart- and replica-safe (the same
-- INSERT ... ON CONFLICT DO NOTHING guard idiom the pause-notify throttle uses). Idempotent.

ALTER TABLE orgs ADD COLUMN IF NOT EXISTS weekly_summary_enabled BOOLEAN  NOT NULL DEFAULT true;
ALTER TABLE orgs ADD COLUMN IF NOT EXISTS weekly_summary_dow     SMALLINT NOT NULL DEFAULT 1;    -- .NET DayOfWeek: 0=Sun..6=Sat, default Monday
ALTER TABLE orgs ADD COLUMN IF NOT EXISTS weekly_summary_hour    SMALLINT NOT NULL DEFAULT 9;    -- local hour 0..23
ALTER TABLE orgs ADD COLUMN IF NOT EXISTS weekly_summary_tz      TEXT     NOT NULL DEFAULT 'UTC'; -- IANA zone id

-- Stamped when an issue is resolved (status -> 2), cleared when it reopens (regression). Powers the weekly
-- "resolved" count; NULL for issues resolved before this migration.
ALTER TABLE issues ADD COLUMN IF NOT EXISTS resolved_at TIMESTAMPTZ;

-- One row per (org, weekly send) that has been handled. The unique PK + INSERT ... ON CONFLICT DO NOTHING is
-- the claim: the worker composes and sends only when it wins the claim, so a persistent tick, a restart, or a
-- second replica cannot re-send the same week.
CREATE TABLE IF NOT EXISTS weekly_summary_sends (
    org_id     BIGINT      NOT NULL REFERENCES orgs (id) ON DELETE CASCADE,
    week_start DATE        NOT NULL,
    sent_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (org_id, week_start)
);
