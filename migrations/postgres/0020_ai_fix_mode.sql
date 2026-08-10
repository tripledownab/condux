-- Org-level AI-fix mode (#101): 0 = manual (a user clicks "Suggest fix"), 1 = auto (the consumer
-- requests a fix when a new/regressed error-or-worse issue lands, quota-gated, still a draft PR).
-- Default manual so the safe behaviour is opt-in. Idempotent.
ALTER TABLE orgs ADD COLUMN IF NOT EXISTS ai_fix_mode SMALLINT NOT NULL DEFAULT 0;
