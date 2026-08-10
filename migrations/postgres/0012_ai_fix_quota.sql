-- Per-org AI-fix quota state (#100 follow-up): the operational counter the RequestFix gate reads and
-- reserves against, instead of counting fix_suggestions rows on every check. One row per org: the current
-- calendar-month period and how many runs were consumed in it. Consumption is an atomic upsert (reserve at
-- request time, race-free under the row lock); a run that later fails or is cancelled is refunded. The
-- fix_suggestions rows + fix_audit remain the reconciliation truth; this row is the cheap live state.
-- Idempotent, applied in filename order after 0011_github_installations.sql.

CREATE TABLE IF NOT EXISTS ai_fix_quota (
    org_id  BIGINT      PRIMARY KEY REFERENCES orgs(id) ON DELETE CASCADE,
    period  DATE        NOT NULL,               -- first day of the calendar month the counter covers
    used    INTEGER     NOT NULL DEFAULT 0,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
