-- Grouped issues (deduplicated events by fingerprint). The consumer upserts here.
-- Full orgs/projects/DSN schema + migration tooling lands in #33.

CREATE TABLE IF NOT EXISTS issues (
    id          BIGSERIAL   PRIMARY KEY,
    project_id  TEXT        NOT NULL,
    fingerprint TEXT        NOT NULL,
    title       TEXT        NOT NULL,
    culprit     TEXT        NOT NULL DEFAULT '',
    level       SMALLINT    NOT NULL DEFAULT 0,
    status      SMALLINT    NOT NULL DEFAULT 1, -- 1=unresolved, 2=resolved, 3=ignored
    first_seen  TIMESTAMPTZ NOT NULL DEFAULT now(),
    last_seen   TIMESTAMPTZ NOT NULL DEFAULT now(),
    event_count BIGINT      NOT NULL DEFAULT 0,
    UNIQUE (project_id, fingerprint)
);

CREATE INDEX IF NOT EXISTS issues_project_last_seen_idx ON issues (project_id, last_seen DESC);
