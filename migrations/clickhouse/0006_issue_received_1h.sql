-- Per-issue event counts by the hour Condux RECEIVED each event, beside condux.issue_stats_1h, which counts
-- by the hour the event itself claims. The weekly digest describes what reached Condux in its week, as its
-- new and regressed counts already do (issues.created_at, reopened_at), so its volume and top issues read
-- this table: a late or backdated event counts in the week it arrived, not the week it names. Charts keep
-- reading issue_stats_1h, where an event belongs to the hour it happened.
--
-- A sibling table rather than a column, because the hour is the sort key, and a table's sort key cannot
-- gain a second time. Same engine as issue_stats_1h, without its first_seen and last_seen: the consumer
-- writes a row of 1 per event, before the sampling decision, and SimpleAggregateFunction(sum) merges them
-- into the hourly sum. Read it with GROUP BY and sum(count), never FINAL.
--
-- Backfilled once, from issue_stats_1h, for the last 15 days and up to now: the digest reads two weeks, so
-- older history would never be read, and an hour still to come is one a sender claimed before ingest
-- bounded the time (a clock set ahead), which this table would otherwise count as received then. Each
-- remaining event's claimed hour stands in for its receipt hour, which holds for every event that was
-- neither late nor early. Every deploy replays this file, so the backfill runs only while the table is
-- empty, and the consumer writes the table only from the release that ships this file, which starts after
-- the migrations have run. What the guard cannot repair: a backfill that fails part way leaves the table
-- non-empty and is not resumed, and events the previous release's consumer counts after the backfill
-- (during the deploy, or for as long as a failed or rolled back upgrade keeps it running) never reach this
-- table. Either costs only the digests whose two weeks include those hours: their volume and top issues,
-- and the digest itself for a week left with no volume, which is then not sent.
-- Idempotent, applied in filename order after 0005_release_modules.sql.

CREATE TABLE IF NOT EXISTS condux.issue_received_1h
(
    project_id String,
    issue_id   UInt64,
    bucket     DateTime                                  CODEC(DoubleDelta, ZSTD(1)),
    count      SimpleAggregateFunction(sum, UInt64)
)
ENGINE = AggregatingMergeTree
PARTITION BY toYYYYMM(bucket)
ORDER BY (project_id, issue_id, bucket)
TTL bucket + INTERVAL 365 DAY;

INSERT INTO condux.issue_received_1h (project_id, issue_id, bucket, count)
SELECT project_id, issue_id, bucket, sum(count)
FROM condux.issue_stats_1h
WHERE (SELECT count() FROM condux.issue_received_1h) = 0
  AND bucket >= now('UTC') - INTERVAL 15 DAY
  AND bucket <= now('UTC')
GROUP BY project_id, issue_id, bucket;
