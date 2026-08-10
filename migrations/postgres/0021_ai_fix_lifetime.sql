-- One-time free AI fixes for the Free tier (#112): a lifetime counter that never resets, independent of
-- the monthly `used`/`period` counter (so switching tiers doesn't reset it). Free gets a small lifetime
-- grant (PlanCatalog.AiFixesLifetime); paid tiers use the monthly allowance and ignore this. Idempotent.
ALTER TABLE ai_fix_quota ADD COLUMN IF NOT EXISTS used_lifetime INTEGER NOT NULL DEFAULT 0;
