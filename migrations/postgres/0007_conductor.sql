-- The Conductor (AI fix engine): a fix run per issue and its audit trail. The provider is pluggable
-- (fake in CI, Anthropic Managed Agents in prod); the safety guarantees live above this in the
-- orchestrator (draft-PR-only, scoped context). See docs/conductor.md + ADR-0010. Idempotent.

CREATE TABLE IF NOT EXISTS fix_suggestions (
    id             UUID        PRIMARY KEY,
    issue_id       BIGINT      NOT NULL REFERENCES issues (id) ON DELETE CASCADE,
    repo_full_name TEXT        NOT NULL,
    status         SMALLINT    NOT NULL DEFAULT 1, -- 1=pending 2=running 3=succeeded 4=failed 5=cancelled
    provider       TEXT        NOT NULL DEFAULT '',
    model          TEXT        NOT NULL DEFAULT '',
    branch         TEXT        NOT NULL DEFAULT '',
    pr_url         TEXT        NOT NULL DEFAULT '',
    summary        TEXT        NOT NULL DEFAULT '',
    created_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at     TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS fix_suggestions_issue_idx ON fix_suggestions (issue_id, created_at DESC);

-- Append-only audit of every step of a run (requested, running, draft_pr_opened, failed, ...).
CREATE TABLE IF NOT EXISTS fix_audit (
    id         BIGSERIAL   PRIMARY KEY,
    fix_id     UUID        NOT NULL REFERENCES fix_suggestions (id) ON DELETE CASCADE,
    actor      TEXT        NOT NULL,
    event      TEXT        NOT NULL,
    detail     JSONB       NOT NULL DEFAULT '{}',
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS fix_audit_fix_idx ON fix_audit (fix_id, created_at);
