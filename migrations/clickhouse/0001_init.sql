-- Condux ClickHouse schema — events store + rollups.
--
-- Storage efficiency is deliberate here, because the events table dominates disk:
--   * ZSTD compression everywhere; LowCardinality for low-arity columns;
--     DoubleDelta for timestamps.
--   * Raw events expire via TTL (90d default) — tune per retention tier.
--   * A rollup materialized view keeps per-issue/per-hour counts so dashboards
--     query the small aggregate table, letting raw events expire sooner.

CREATE DATABASE IF NOT EXISTS condux;

-- ---------------------------------------------------------------------------
-- Raw events (detail view + ad-hoc queries). Short retention.
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS condux.events
(
    project_id      String                               CODEC(ZSTD(1)),
    issue_id        UInt64                               CODEC(ZSTD(1)),
    event_id        FixedString(32)                      CODEC(ZSTD(1)),
    timestamp       DateTime64(3)                        CODEC(DoubleDelta, ZSTD(1)),
    received_at     DateTime64(3) DEFAULT now64(3)       CODEC(DoubleDelta, ZSTD(1)),
    level           LowCardinality(String)               CODEC(ZSTD(1)),
    platform        LowCardinality(String)               CODEC(ZSTD(1)),
    environment     LowCardinality(String)               CODEC(ZSTD(1)),
    release         LowCardinality(String)               CODEC(ZSTD(1)),
    server_name     String                               CODEC(ZSTD(1)),
    transaction     String                               CODEC(ZSTD(1)),
    message         String                               CODEC(ZSTD(1)),
    exception_type  LowCardinality(String)               CODEC(ZSTD(1)),
    exception_value String                               CODEC(ZSTD(1)),
    fingerprint     String                               CODEC(ZSTD(1)),
    tags            Map(LowCardinality(String), String)  CODEC(ZSTD(1)),
    -- full normalized (already PII-scrubbed) payload for the detail view
    payload         String                               CODEC(ZSTD(3))
)
ENGINE = MergeTree
PARTITION BY toYYYYMMDD(timestamp)
ORDER BY (project_id, issue_id, timestamp)
TTL toDateTime(timestamp) + INTERVAL 90 DAY
SETTINGS index_granularity = 8192;

-- ---------------------------------------------------------------------------
-- Rollup: per-issue, per-hour counts. Longer retention, tiny footprint.
-- Dashboards read this instead of scanning condux.events.
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS condux.issue_stats_1h
(
    project_id String,
    issue_id   UInt64,
    bucket     DateTime                                  CODEC(DoubleDelta, ZSTD(1)),
    count      SimpleAggregateFunction(sum, UInt64),
    first_seen SimpleAggregateFunction(min, DateTime64(3)),
    last_seen  SimpleAggregateFunction(max, DateTime64(3))
)
ENGINE = AggregatingMergeTree
PARTITION BY toYYYYMM(bucket)
ORDER BY (project_id, issue_id, bucket)
TTL bucket + INTERVAL 365 DAY;

CREATE MATERIALIZED VIEW IF NOT EXISTS condux.issue_stats_1h_mv
TO condux.issue_stats_1h
AS
SELECT
    project_id,
    issue_id,
    toStartOfHour(timestamp) AS bucket,
    count()        AS count,
    min(timestamp) AS first_seen,
    max(timestamp) AS last_seen
FROM condux.events
GROUP BY project_id, issue_id, bucket;
