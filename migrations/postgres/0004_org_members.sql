-- Org membership + RBAC (#48). A user belongs to an org with a role; this is the tenancy +
-- authorization boundary for the control-plane API. Signup created a personal org (owner) when this
-- migration was written. ADR-0018 later moved that into onboarding, so a user may belong to no org.
-- role: 0=member, 1=admin, 2=owner (Condux.Core.Auth.OrgRole; ordered so role >= required gates access).
-- Idempotent (IF NOT EXISTS), applied in filename order after 0003_users_sessions.sql.

CREATE TABLE IF NOT EXISTS org_members (
    id         BIGSERIAL   PRIMARY KEY,
    org_id     BIGINT      NOT NULL REFERENCES orgs(id) ON DELETE CASCADE,
    user_id    BIGINT      NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    role       SMALLINT    NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (org_id, user_id)
);

-- Membership is looked up per request by (org_id, user_id) for authz, and by user_id to list a
-- user's orgs; the UNIQUE(org_id, user_id) index covers the former.
CREATE INDEX IF NOT EXISTS org_members_user_idx ON org_members (user_id);
