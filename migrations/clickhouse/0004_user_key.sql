-- Pseudonymous user key per event (#105): a hash of the strongest user identifier, derived at ingest
-- before the raw identifiers are scrubbed away (see Condux.Core.Scrub.UserKeys). Powers distinct
-- "users affected" counts (uniqExact over stored events — a sample when the issue is past the sampling
-- threshold, so the UI labels it as such). Empty when the event carried no user.
-- Idempotent, applied in filename order after 0003_exact_issue_stats.sql.

ALTER TABLE condux.events ADD COLUMN IF NOT EXISTS user_key String CODEC(ZSTD(1));
