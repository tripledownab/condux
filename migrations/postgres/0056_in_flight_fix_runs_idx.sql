-- The stale-run sweep reads the hosted fix runs still pending or running, every few minutes on every
-- conductor. Those are a handful of rows in tables that only grow, so a partial index over just them keeps
-- the sweep from scanning every run ever made. The predicate matches the sweep's own WHERE clause, which
-- is what lets the planner use it.
CREATE INDEX IF NOT EXISTS fix_suggestions_in_flight_idx
    ON fix_suggestions (updated_at) WHERE status IN (1, 2) AND job_context IS NULL;
CREATE INDEX IF NOT EXISTS cve_fix_runs_in_flight_idx
    ON cve_fix_runs (updated_at) WHERE status IN (1, 2) AND job_context IS NULL;
