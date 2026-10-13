-- Which request a fix run came from, and when that request reserved its allowance.
--
-- job_id is minted once, where the request reserves its run, and rides the job. A job delivered twice (the
-- conductor took it, died before its offset committed, and the broker handed it out again) therefore meets
-- its own row: the insert's ON CONFLICT on this unique index writes nothing, instead of a second run and a
-- second draft PR on one reservation. Rows written before the column carry NULL, which a unique index
-- never treats as a duplicate.
--
-- reserved_at is the instant the allowance was reserved. A refund returns the run to that month. On a run
-- the conductor executes, created_at is when it took the job, which can fall in the next month. NULL on
-- older rows, where readers fall back to created_at.
ALTER TABLE fix_suggestions ADD COLUMN IF NOT EXISTS job_id UUID;
ALTER TABLE fix_suggestions ADD COLUMN IF NOT EXISTS reserved_at TIMESTAMPTZ;
CREATE UNIQUE INDEX IF NOT EXISTS fix_suggestions_job_id_uq ON fix_suggestions (job_id);

ALTER TABLE cve_fix_runs ADD COLUMN IF NOT EXISTS job_id UUID;
ALTER TABLE cve_fix_runs ADD COLUMN IF NOT EXISTS reserved_at TIMESTAMPTZ;
CREATE UNIQUE INDEX IF NOT EXISTS cve_fix_runs_job_id_uq ON cve_fix_runs (job_id);
