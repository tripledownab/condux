-- Scoped MCP tokens: a per-project machine credential so an AI agent can read a project's issues over
-- the MCP endpoint (POST /api/mcp, Authorization: Bearer) without a cookie login. Read-only, scoped to
-- one project. Only the SHA-256 hash of the token is stored; the raw value is shown once at creation.
-- Mirrors release_tokens (0032).
CREATE TABLE IF NOT EXISTS mcp_tokens (
    id           UUID        PRIMARY KEY,
    project_id   BIGINT      NOT NULL REFERENCES projects (id) ON DELETE CASCADE,
    token_hash   TEXT        NOT NULL UNIQUE,
    name         TEXT        NOT NULL,
    created_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
    last_used_at TIMESTAMPTZ,
    revoked_at   TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS mcp_tokens_project_idx ON mcp_tokens (project_id, created_at DESC);
