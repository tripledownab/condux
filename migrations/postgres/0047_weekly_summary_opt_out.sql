-- Per-user opt-out from the weekly summary digest.
--
-- The digest is enabled per ORG (orgs.weekly_summary_enabled) and then goes to every member. A member who
-- did not want it had to ask an admin to switch it off for everyone, which is the wrong lever: one
-- person's inbox preference should not decide what the rest of the org receives.
--
-- Stored as an opt-out rather than a subscription so the default is "changed nothing": false means nobody
-- has opted out, every existing member keeps receiving exactly what they received before this migration,
-- and no backfill is needed. Idempotent.
ALTER TABLE users ADD COLUMN IF NOT EXISTS weekly_summary_opt_out boolean NOT NULL DEFAULT false;
