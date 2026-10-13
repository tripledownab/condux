-- At most one fix run in progress (1 pending, 2 running) per issue, as cve_fix_runs_active_uq already
-- holds one per repository and advisory. A hosted request now writes its pending run before it publishes
-- the job, so this index is what refuses a second request for the same issue while the first is queued
-- or running, instead of letting both reach the conductor and open two draft PRs.
--
-- Nothing stopped two runs for one issue before this, so a database may already hold some, and the index
-- cannot be built over them. One transaction holds a lock that blocks writes to the table from before the
-- cleanup until the index exists, because the previous release keeps serving during a deploy and a
-- duplicate it wrote in between would fail the build.
--
-- One run per issue is kept: a running one before a pending one, since a running one may have a worker
-- or a runner on it, then the newest. The rest are cancelled (5) rather than failed: a failed run with the
-- interrupted reason reads as one the stale-run sweep refunded, and a success arriving after it would be
-- charged again. They are not refunded here: which month each was reserved in is beyond a migration.
-- Once the index exists no second run can be written, so the cleanup finds nothing on every later replay.
BEGIN;

LOCK TABLE fix_suggestions IN SHARE ROW EXCLUSIVE MODE;

UPDATE fix_suggestions fs
SET status = 5, updated_at = now()
WHERE fs.status IN (1, 2)
  AND EXISTS (
      SELECT 1 FROM fix_suggestions kept
      WHERE kept.issue_id = fs.issue_id
        AND kept.status IN (1, 2)
        AND (kept.status = 2, kept.created_at, kept.id) > (fs.status = 2, fs.created_at, fs.id));

CREATE UNIQUE INDEX IF NOT EXISTS fix_suggestions_active_uq
    ON fix_suggestions (issue_id) WHERE status IN (1, 2);

COMMIT;
