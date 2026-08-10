-- Scoped release tokens: a per-project machine credential so CI can record a release through the API
-- (POST /api/releases, Authorization: Bearer) without a cookie login or internal ids. Only the SHA-256
-- hash of the token is stored; the raw value is shown to the operator once at creation.
CREATE TABLE IF NOT EXISTS release_tokens (
    id           UUID        PRIMARY KEY,
    project_id   BIGINT      NOT NULL REFERENCES projects (id) ON DELETE CASCADE,
    token_hash   TEXT        NOT NULL UNIQUE,
    name         TEXT        NOT NULL,
    created_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
    last_used_at TIMESTAMPTZ,
    revoked_at   TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS release_tokens_project_idx ON release_tokens (project_id, created_at DESC);
