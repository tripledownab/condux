-- Org invites (#84). An org admin/owner invites an email to join with a role; the invitee accepts
-- with an opaque token (stored only as a hash, like sessions) once logged in as that email.
-- Idempotent (IF NOT EXISTS), applied in filename order after 0004_org_members.sql.

CREATE TABLE IF NOT EXISTS org_invites (
    id          BIGSERIAL   PRIMARY KEY,
    org_id      BIGINT      NOT NULL REFERENCES orgs(id) ON DELETE CASCADE,
    email       TEXT        NOT NULL,               -- normalized (trimmed + lower-cased)
    role        SMALLINT    NOT NULL,               -- Condux.Core.Auth.OrgRole
    token_hash  TEXT        NOT NULL UNIQUE,        -- SHA-256 of the raw invite token
    invited_by  BIGINT      REFERENCES users(id) ON DELETE SET NULL,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    expires_at  TIMESTAMPTZ NOT NULL,
    accepted_at TIMESTAMPTZ,
    revoked_at  TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS org_invites_org_idx ON org_invites (org_id);
