-- Exact per-issue stats (#102). The issue_stats_1h rollup was fed by a materialized view over
-- condux.events, which only sees the rows the consumer stores AFTER per-issue sampling (first N, then
-- 1-in-M) — so the rollup undercounted busy issues. Counting moves app-side: the consumer now inserts a
-- tiny stats row into issue_stats_1h for EVERY event (SimpleAggregateFunction(sum) merges them into
-- hourly sums), before the sampling decision. Drop the view so stored events are not counted twice.
-- Do not re-add a materialized view on condux.events for counts — it can only ever see sampled rows.
-- Rollup rows written before this migration are approximate (they undercounted); they are left in place.
-- Idempotent, applied in filename order after 0002_per_tier_retention.sql.

DROP VIEW IF EXISTS condux.issue_stats_1h_mv;
