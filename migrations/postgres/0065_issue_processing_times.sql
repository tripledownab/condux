-- Time an issue's lifecycle by Condux's clock, never by the event's.
--
-- first_seen and last_seen are the times events claim. The sender chooses them and an offline client
-- delivers them late, so a question about when something happened TO CONDUX cannot read them: a late event
-- landed behind a user's last view and never reached the new-issues badge, and a late first event never
-- counted as new in any weekly digest. The upsert now stamps activated_at with now(), and two columns
-- carry the rest:
--   created_at  when Condux first processed the issue; the digest's "new" reads it.
--   reopened_at when an event last reopened a resolved issue; the digest's "regressed" reads it. A manual
--               unresolve never counted as a regression and still does not.
--
-- Neither is backfilled. A backfill rewrites every row of issues under the ACCESS EXCLUSIVE lock that
-- ADD COLUMN takes, stalling every reader and the consumer for as long as that takes, and it would have to
-- be guarded to run once because any change to the migration files but new ones sorting last replays
-- it. A row from before this migration keeps both NULL instead, and the digest reads it by the rule that
-- applied when it was written
-- (IssueRepository.WeeklyIssueCountsAsync). Adding a nullable column and then a default is a catalog change
-- only: the default applies to rows inserted from here on, including by a consumer still running the
-- previous code during a deploy.
ALTER TABLE issues ADD COLUMN IF NOT EXISTS created_at TIMESTAMPTZ;
ALTER TABLE issues ALTER COLUMN created_at SET DEFAULT now();
ALTER TABLE issues ADD COLUMN IF NOT EXISTS reopened_at TIMESTAMPTZ;
