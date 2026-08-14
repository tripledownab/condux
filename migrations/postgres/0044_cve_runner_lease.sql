-- Route CVE-bump runs to a customer's runner too (ADR-0033 follow-up): the same lease + job-context
-- columns fix_suggestions gained in 0040/0041, for the same reasons. job_context both carries the work
-- (base branch + assembled bump prompt + manifest paths) and marks the row as a runner's to take — a
-- hosted CVE run keeps it NULL and is never claimable. leased_by is the server-generated lease id;
-- lease_expires_at is what makes a dead runner's work reclaimable. Idempotent.

ALTER TABLE cve_fix_runs
    ADD COLUMN IF NOT EXISTS job_context       JSONB,
    ADD COLUMN IF NOT EXISTS leased_by         TEXT,
    ADD COLUMN IF NOT EXISTS leased_at         TIMESTAMPTZ,
    ADD COLUMN IF NOT EXISTS lease_expires_at  TIMESTAMPTZ;

-- Partial, like fix_suggestions_claimable_idx: a claimable bump is a vanishing fraction of the table.
CREATE INDEX IF NOT EXISTS cve_fix_runs_claimable_idx
    ON cve_fix_runs (created_at)
    WHERE job_context IS NOT NULL AND status IN (1, 2);
