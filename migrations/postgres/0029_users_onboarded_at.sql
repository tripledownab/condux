-- Onboarding completion is an explicit signal (the user clicked Finish, or joined an org via invite), not
-- something derivable from data — a fresh owner has an org + a project the moment before they finish just
-- as after. So it is stored here. NULL = not yet onboarded; the dashboard gate keeps such a user on
-- /onboarding. Idempotent.
ALTER TABLE users ADD COLUMN IF NOT EXISTS onboarded_at timestamptz;

-- Grandfather every existing user who already belongs to an organization: they predate this gate and are
-- clearly past onboarding, so they must not be bounced back into it. Users with no org (e.g. an abandoned
-- signup) stay NULL and will walk onboarding to create their tenant.
UPDATE users
SET onboarded_at = created_at
WHERE onboarded_at IS NULL
  AND id IN (SELECT DISTINCT user_id FROM org_members);
