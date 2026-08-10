-- GitHub App installations (#61). When someone installs the Conductor's GitHub App and returns through
-- the Setup URL, we tie the GitHub installation_id to a Condux org here; the org's short-lived
-- installation tokens are minted on demand from this mapping. One installation belongs to one org.
-- Idempotent, applied in filename order after 0010_issue_project_fk.sql.

CREATE TABLE IF NOT EXISTS github_installations (
    id              BIGSERIAL   PRIMARY KEY,
    installation_id BIGINT      NOT NULL UNIQUE,
    org_id          BIGINT      NOT NULL REFERENCES orgs(id) ON DELETE CASCADE,
    account_login   TEXT,                                   -- the GitHub org/user the app is installed on
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS github_installations_org_idx ON github_installations (org_id);
