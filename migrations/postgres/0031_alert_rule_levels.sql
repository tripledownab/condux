-- Per-level alert severity (replaces the min_level threshold). A rule now fires on any of its selected
-- levels, so a user can pick an exact combination (Info/Warning/Error/Fatal) instead of a ">= minimum",
-- whose cascade made it easy for two rules to overlap and double-deliver. Idempotent, like the others.

ALTER TABLE alert_rules ADD COLUMN IF NOT EXISTS levels SMALLINT[];

-- Backfill from the old threshold (the levels a min_level rule covered = min_level .. Fatal(5)), then drop
-- it. Guarded so a re-run (after the column is already gone) is a no-op.
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_name = 'alert_rules' AND column_name = 'min_level'
    ) THEN
        UPDATE alert_rules SET levels = ARRAY(SELECT generate_series(min_level, 5))::smallint[];
        ALTER TABLE alert_rules DROP COLUMN min_level;
    END IF;
END $$;

-- Error + Fatal by default (matches the old min_level default of Error); defensive, the app always sets it.
ALTER TABLE alert_rules ALTER COLUMN levels SET DEFAULT ARRAY[4, 5]::smallint[];
UPDATE alert_rules SET levels = ARRAY[4, 5]::smallint[] WHERE levels IS NULL;
ALTER TABLE alert_rules ALTER COLUMN levels SET NOT NULL;
