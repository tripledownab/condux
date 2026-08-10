-- Source-map artifacts (ADR-0028): uploaded client source maps the control-plane uses to de-minify
-- (symbolicate) stack frames at read time. The map file itself lives in object storage (S3/MinIO) under
-- object_key; this table indexes it by the keys symbolication resolves on: debug_id (primary, robust
-- across deploys) and the (release, dist, filename) tuple (fallback for a plain CI .map upload). Uploaded
-- by CI through the scoped release token, the same credential that records a release.
CREATE TABLE IF NOT EXISTS sourcemap_artifacts (
    id          UUID        PRIMARY KEY,
    project_id  BIGINT      NOT NULL REFERENCES projects (id) ON DELETE CASCADE,
    release     TEXT        NOT NULL,
    dist        TEXT,
    debug_id    TEXT,
    filename    TEXT        NOT NULL,
    object_key  TEXT        NOT NULL,
    byte_size   BIGINT      NOT NULL,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (project_id, object_key)
);

-- Resolution paths: debug_id first (robust), then the (release, dist, filename) fallback.
CREATE INDEX IF NOT EXISTS sourcemap_artifacts_debug_idx
    ON sourcemap_artifacts (project_id, debug_id) WHERE debug_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS sourcemap_artifacts_release_idx
    ON sourcemap_artifacts (project_id, release, filename);
