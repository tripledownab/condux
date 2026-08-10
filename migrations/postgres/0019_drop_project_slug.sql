-- projects.slug was vestigial: never a lookup key, not in the DSN or any URL (ingest uses the numeric
-- id, the dashboard uses public_id). Drop it. Dropping the column also drops the UNIQUE (org_id, slug)
-- index that rode on it. Idempotent.
ALTER TABLE projects DROP COLUMN IF EXISTS slug;
