-- Platform-admin audit trail (ADR-0027): a record of every mutating action a platform operator takes in
-- the super-admin console (org edit/rename, member role/remove, billing cancel/plan/portal, and the
-- start/stop of a read-only "view as org" impersonation session). ADR-0014 shipped the console read-only
-- and explicitly deferred mutations to "a table plus an audit trail" -- this is that table.
--
-- Deliberately has NO foreign keys: the audit record must outlive deletion of the actor, the target org,
-- or the target member (retention beats referential tidiness -- the point of an audit log is that it
-- survives the thing it recorded). actor_id/actor_email are always the admin's REAL identity, even while
-- impersonating. details carries per-action context (before/after values, Stripe ids). Idempotent
-- (IF NOT EXISTS), applied in filename order after 0027_org_billing.sql.

CREATE TABLE IF NOT EXISTS admin_audit (
    id             BIGSERIAL   PRIMARY KEY,
    actor_id       BIGINT      NOT NULL,   -- the platform admin's real user id (even mid-impersonation)
    actor_email    TEXT        NOT NULL,
    action         TEXT        NOT NULL,   -- org.rename | org.settings | member.role | member.remove
                                           -- | billing.cancel | billing.plan_change | billing.portal
                                           -- | impersonation.start | impersonation.stop
    target_org_id  BIGINT,                 -- nullable: the org acted on / viewed
    target_user_id BIGINT,                 -- nullable: the member acted on
    details        JSONB       NOT NULL DEFAULT '{}'::jsonb,
    created_at     TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- The audit tab reads newest-first, optionally filtered to one org.
CREATE INDEX IF NOT EXISTS admin_audit_created_idx ON admin_audit (created_at DESC);
CREATE INDEX IF NOT EXISTS admin_audit_org_idx     ON admin_audit (target_org_id, created_at DESC);
