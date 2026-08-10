-- Per-user saved issue views (#108): a named search query + ordering over a project's issues.
-- Deleting the user or the project takes the views with it.
CREATE TABLE IF NOT EXISTS saved_views (
    id         BIGSERIAL   PRIMARY KEY,
    user_id    BIGINT      NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    project_id BIGINT      NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    name       TEXT        NOT NULL,
    query      TEXT        NOT NULL DEFAULT '',
    sort       TEXT        NOT NULL DEFAULT 'lastSeen',
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (user_id, project_id, name)
);

-- Listed per user per project on every issues-surface load.
CREATE INDEX IF NOT EXISTS saved_views_user_project ON saved_views (user_id, project_id);
