-- The CVE-bump jobs the conductor took and did not run because another run was in progress on their
-- advisory, and whose reservation it handed back.
--
-- A refused job writes no run, so without a record of it a second delivery of the same job (the conductor
-- died before its offset committed) would be refunded again, or, once the advisory was free, would run on
-- the reservation it had handed back. The refund is paid only by the insert that writes this row, in the
-- same transaction, and the run insert refuses any job recorded here.
CREATE TABLE IF NOT EXISTS cve_fix_skipped_jobs (
    job_id     UUID        PRIMARY KEY,
    skipped_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
