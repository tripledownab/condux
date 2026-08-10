-- Conductor cost cap (#120 budgets): an optional per-org monthly spend ceiling in USD. Null means no
-- cap (the default). When set, RequestFix and auto-fix refuse a new run once the org's month-to-date
-- Conductor spend (priced from ModelPricing) has reached it. Governs platform-billed spend only; a
-- bring-your-own-key org's usage is on its own provider account and is not priced, so it never trips.

ALTER TABLE orgs ADD COLUMN IF NOT EXISTS ai_fix_cost_cap_usd NUMERIC;
