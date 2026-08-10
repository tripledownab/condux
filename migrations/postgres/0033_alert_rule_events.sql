-- Per-event alert subscription (replaces the on_new_issue/on_regression booleans). A rule now fires on any
-- of its selected event types (NewIssue=1, Regression=2, Resolved=3, Assigned=4), so it can subscribe to a
-- list of events instead of just the two triggers. Idempotent, mirrors 0031_alert_rule_levels.sql.

ALTER TABLE alert_rules ADD COLUMN IF NOT EXISTS events SMALLINT[];

-- Backfill from the old booleans (on_new_issue -> 1, on_regression -> 2), then drop them. Guarded so a
-- re-run (after the columns are already gone) is a no-op.
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_name = 'alert_rules' AND column_name = 'on_new_issue'
    ) THEN
        UPDATE alert_rules SET events =
            ((CASE WHEN on_new_issue THEN ARRAY[1] ELSE ARRAY[]::int[] END
              || CASE WHEN on_regression THEN ARRAY[2] ELSE ARRAY[]::int[] END))::smallint[];
        ALTER TABLE alert_rules DROP COLUMN on_new_issue;
        ALTER TABLE alert_rules DROP COLUMN on_regression;
    END IF;
END $$;

-- New issue + regression by default (matches the old both-true default); defensive, the app always sets it.
ALTER TABLE alert_rules ALTER COLUMN events SET DEFAULT ARRAY[1, 2]::smallint[];
UPDATE alert_rules SET events = ARRAY[1, 2]::smallint[] WHERE events IS NULL;
ALTER TABLE alert_rules ALTER COLUMN events SET NOT NULL;
