-- Control-plane catalog: organizations, projects, and DSN keys.
-- Tier values match Condux.Core.Plans.Tier (0=Free, 1=Team, 2=Business, 3=Enterprise).
-- Idempotent (IF NOT EXISTS), applied in filename order after 0001_issues.sql.
-- (users + RBAC land with auth, not before.)

CREATE TABLE IF NOT EXISTS orgs (
    id         BIGSERIAL   PRIMARY KEY,
    slug       TEXT        NOT NULL UNIQUE,
    name       TEXT        NOT NULL,
    tier       SMALLINT    NOT NULL DEFAULT 0,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS projects (
    id         BIGSERIAL   PRIMARY KEY,
    org_id     BIGINT      NOT NULL REFERENCES orgs(id) ON DELETE CASCADE,
    slug       TEXT        NOT NULL,
    name       TEXT        NOT NULL,
    platform   TEXT        NOT NULL DEFAULT 'other',
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (org_id, slug)
);

CREATE INDEX IF NOT EXISTS projects_org_idx ON projects (org_id);

-- DSN public keys. A project can have several (rotation); the relay accepts any active one.
CREATE TABLE IF NOT EXISTS dsn_keys (
    id         BIGSERIAL   PRIMARY KEY,
    project_id BIGINT      NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    public_key TEXT        NOT NULL UNIQUE,
    label      TEXT        NOT NULL DEFAULT 'default',
    is_active  BOOLEAN     NOT NULL DEFAULT true,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    revoked_at TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS dsn_keys_project_idx ON dsn_keys (project_id);
-- The relay authenticates by (project_id, public_key) among active keys.
CREATE INDEX IF NOT EXISTS dsn_keys_active_lookup_idx ON dsn_keys (project_id, public_key) WHERE is_active;
