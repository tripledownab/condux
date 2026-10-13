-- CVE bump verification (ADR-0051): after a bump's draft PR merges, the run is watched against the
-- runtime dependency inventory and concludes held, still exposed or cannot verify, with its evidence.
-- verify_status: 0=none 1=watching 2=held 3=still exposed (did_not_hold) 4=cannot verify
ALTER TABLE cve_fix_runs ADD COLUMN IF NOT EXISTS merged_at       TIMESTAMPTZ;
ALTER TABLE cve_fix_runs ADD COLUMN IF NOT EXISTS verify_status   SMALLINT NOT NULL DEFAULT 0;
ALTER TABLE cve_fix_runs ADD COLUMN IF NOT EXISTS verified_at     TIMESTAMPTZ;
-- What the conclusion rested on: the sightings and how each was classed, the ranges and target used.
ALTER TABLE cve_fix_runs ADD COLUMN IF NOT EXISTS verify_evidence JSONB;
-- Every vulnerable range the advisory gives the run's package, its own from_range included, stored when
-- the run is requested. A run requested before this column has none, and if it merges after it,
-- concludes cannot verify: one range alone can call a version fixed that another range of the same
-- advisory calls vulnerable.
ALTER TABLE cve_fix_runs ADD COLUMN IF NOT EXISTS vulnerable_ranges TEXT[];

-- The watcher scans watching runs on a timer.
CREATE INDEX IF NOT EXISTS cve_fix_runs_watching ON cve_fix_runs (verify_status) WHERE verify_status = 1;
