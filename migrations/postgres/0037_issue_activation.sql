-- New-issue nav badge (ADR-0030). Two pieces:
--   1. issues.activated_at — when an issue last "became new": its first_seen on insert, bumped to the
--      reopening event's time on a 2->1 regression (stamped by the consumer's upsert). Lets the badge
--      count issues that are new OR regressed since a user last looked, not just brand-new ones.
--   2. issue_seen — a per (user, project) watermark of when the user last opened the issues list, so the
--      "new issues" badge is per-user (each viewer clears their own), mirroring the fixes badge.
-- Idempotent (safe to re-run).

ALTER TABLE issues ADD COLUMN IF NOT EXISTS activated_at TIMESTAMPTZ;

-- Backfill existing rows so the count has a value to compare against (their first appearance).
UPDATE issues SET activated_at = first_seen WHERE activated_at IS NULL;

CREATE TABLE IF NOT EXISTS issue_seen (
    user_id    BIGINT      NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    project_id BIGINT      NOT NULL REFERENCES projects (id) ON DELETE CASCADE,
    seen_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (user_id, project_id)
);
