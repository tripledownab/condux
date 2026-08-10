-- Connect-repo + error→code linking (#89, ADR-0011): which GitHub repo backs a project, the code
-- mappings that turn a runtime stack-frame path into a repo path, and the release→commit association
-- so a fix runs against the exact code. The GitHub App install + installation tokens (which populate
-- and read these) land in #61. Public ids are UUIDs (non-enumerable); FKs to projects stay internal.
-- Idempotent.

CREATE TABLE IF NOT EXISTS repo_links (
    id             UUID        PRIMARY KEY,
    project_id     BIGINT      NOT NULL REFERENCES projects (id) ON DELETE CASCADE,
    repo_full_name TEXT        NOT NULL,
    default_branch TEXT        NOT NULL DEFAULT 'main',
    created_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (project_id, repo_full_name)
);

CREATE INDEX IF NOT EXISTS repo_links_project_idx ON repo_links (project_id);

-- stack_root → source_root prefix rules: /app/dist/ → src/ turns /app/dist/checkout.js into src/checkout.js.
CREATE TABLE IF NOT EXISTS code_mappings (
    id           UUID        PRIMARY KEY,
    repo_link_id UUID        NOT NULL REFERENCES repo_links (id) ON DELETE CASCADE,
    stack_root   TEXT        NOT NULL,
    source_root  TEXT        NOT NULL DEFAULT '',
    created_at   TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS code_mappings_repo_idx ON code_mappings (repo_link_id);

-- A release tied to the commit it deployed, so a fix checks out and diffs against the right ref.
CREATE TABLE IF NOT EXISTS releases (
    id           UUID        PRIMARY KEY,
    project_id   BIGINT      NOT NULL REFERENCES projects (id) ON DELETE CASCADE,
    repo_link_id UUID        NOT NULL REFERENCES repo_links (id) ON DELETE CASCADE,
    version      TEXT        NOT NULL,
    commit_sha   TEXT        NOT NULL,
    created_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (project_id, version)
);

CREATE INDEX IF NOT EXISTS releases_project_idx ON releases (project_id, created_at DESC);
