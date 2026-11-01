-- Advisory alerts: a dependency advisory GitHub newly raises on a linked repository fires the project's
-- alert rules. One row per repository link, advisory, affected package and UTC day it notified, so neither
-- a redelivery nor the alert GitHub raises per manifest of a monorepo notifies anyone twice in a day,
-- while an advisory naming two packages still names each.
CREATE TABLE IF NOT EXISTS advisory_notices (
    repo_link_id UUID        NOT NULL REFERENCES repo_links(id) ON DELETE CASCADE,
    ghsa_id      TEXT        NOT NULL,
    package      TEXT        NOT NULL,
    day          DATE        NOT NULL,
    project_id   BIGINT      NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    created_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (repo_link_id, ghsa_id, package, day)
);

CREATE INDEX IF NOT EXISTS advisory_notices_project_idx ON advisory_notices (project_id);
