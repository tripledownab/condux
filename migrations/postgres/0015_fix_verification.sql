-- Fix verification (ADR-0019): after a Conductor draft PR merges, the run is watched against the
-- issue's exact stats; a clean window resolves the issue with evidence, occurrences flag the fix.
-- verify_status: 0=none 1=watching 2=held 3=did_not_hold
ALTER TABLE fix_suggestions ADD COLUMN IF NOT EXISTS merged_at     TIMESTAMPTZ;
ALTER TABLE fix_suggestions ADD COLUMN IF NOT EXISTS verify_status SMALLINT NOT NULL DEFAULT 0;
ALTER TABLE fix_suggestions ADD COLUMN IF NOT EXISTS verified_at   TIMESTAMPTZ;

-- The watcher scans watching fixes on a timer.
CREATE INDEX IF NOT EXISTS fix_suggestions_watching ON fix_suggestions (verify_status) WHERE verify_status = 1;
