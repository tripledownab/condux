-- Make issues.project_id a real BIGINT foreign key to projects(id), instead of TEXT (#97, ADR-0015).
-- project_id has always held a stringified bigint: it enters as the URL path segment of the Sentry
-- ingest route and mirrors ClickHouse events.project_id (which stays String — its sort key, no FKs).
-- The relay DSN-authenticates every event against projects BEFORE publishing, so the referenced
-- project row always exists; the FK adds referential integrity (drop a project → its issues cascade)
-- and lets issues join projects/orgs without a ::bigint cast (admin console, per-org quotas #73).
-- Idempotent: the type change is guarded on the current type, and the constraint is dropped/re-added.

DO $$
BEGIN
    IF (SELECT data_type FROM information_schema.columns
        WHERE table_name = 'issues' AND column_name = 'project_id') = 'text' THEN
        ALTER TABLE issues ALTER COLUMN project_id TYPE BIGINT USING project_id::bigint;
    END IF;
END $$;

ALTER TABLE issues DROP CONSTRAINT IF EXISTS issues_project_id_fkey;
ALTER TABLE issues
    ADD CONSTRAINT issues_project_id_fkey
    FOREIGN KEY (project_id) REFERENCES projects (id) ON DELETE CASCADE;

-- The (project_id, fingerprint) unique constraint and issues_project_last_seen_idx carry over the
-- retyped column automatically; the FK's referencing column is already covered by those for cascade.
