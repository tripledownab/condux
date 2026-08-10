-- Conductor cost visibility (#120): persist the model token usage on the run row so per-project and
-- per-model spend is a plain aggregate. The counts also ride the draft_pr_opened audit entry (#100);
-- the row is the queryable operational copy. Cost is derived from ModelPricing at read time, so a rate
-- correction reprices history without a backfill. 0 for the no-model backends (fake/simulated).

ALTER TABLE fix_suggestions ADD COLUMN IF NOT EXISTS input_tokens  BIGINT NOT NULL DEFAULT 0;
ALTER TABLE fix_suggestions ADD COLUMN IF NOT EXISTS output_tokens BIGINT NOT NULL DEFAULT 0;
