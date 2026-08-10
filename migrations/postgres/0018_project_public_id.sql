-- Public, non-enumerable project identifier for dashboard URLs and the project resource API (get /
-- rename / delete). The BIGSERIAL id stays INTERNAL: it is the ingest identity (the DSN + the
-- /api/{projectId}/store/ URL, the Kafka key, ClickHouse events.project_id, the issues.project_id FK),
-- so it cannot move. Only public_id is exposed in a browser URL, so /projects/<uuid> no longer leaks
-- ordering or count. New projects get a UUIDv7 from the app (time-ordered, so this unique index stays
-- healthy); existing rows are backfilled with gen_random_uuid() (v4). Both are opaque. Idempotent.
ALTER TABLE projects ADD COLUMN IF NOT EXISTS public_id UUID NOT NULL DEFAULT gen_random_uuid();

CREATE UNIQUE INDEX IF NOT EXISTS projects_public_id_idx ON projects (public_id);
