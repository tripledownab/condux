-- Per-tier retention (#73). Raw events expire on a schedule that varies by the project's plan tier
-- (Free 30d, paid 90d) instead of a flat 90d. retention_days is stamped per row at ingest from the tier
-- (relay computes it, passes it as a Kafka header, the consumer writes it), and the table TTL references
-- the column — so ClickHouse's background merges do the expiry themselves, with no cron or scheduler.
-- Existing rows default to 90 (unchanged behavior). Idempotent (guarded ADD COLUMN + repeatable MODIFY TTL).

-- DEFAULT mirrors Condux.Core.Plans.PlanCatalog.DefaultRetentionDays (SQL can't reference the constant);
-- it only applies to rows that predate per-tier retention, since the consumer now always writes a value.
ALTER TABLE condux.events
    ADD COLUMN IF NOT EXISTS retention_days UInt16 DEFAULT 90;

-- Column-driven TTL: each row expires retention_days after its own timestamp.
ALTER TABLE condux.events
    MODIFY TTL toDateTime(timestamp) + toIntervalDay(retention_days);
