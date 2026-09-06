-- Runtime dependency inventory (ADR-0041). The Sentry wire format carries a `modules` map of installed
-- dependency versions. SentryParser has always read it into Event.Modules, and it has always been
-- serialized into the events payload blob along with the rest of the event, but it has never been
-- indexed and nothing has ever queried it.
-- This is where it becomes queryable: which package version was actually LOADED, in which release and
-- environment, as opposed to which one a manifest declares. That difference is the whole point, because
-- a manifest read cannot see transitive resolution, lockfile drift, or a deploy that does not match the
-- branch somebody scanned.
--
-- Why a dedicated table and not a Map column on condux.events. A module set repeats almost exactly on
-- every event from the same release, so storing it per event would multiply the hot table by the
-- dependency count to hold a few hundred distinct rows. Here one row per sort key, the whole of
-- (project_id, release, environment, ecosystem, package, version), is written once and merged after.
--
-- Engine mirrors condux.issue_stats_1h rather than inventing a shape: AggregatingMergeTree with
-- SimpleAggregateFunction, so repeated inserts of the same tuple merge into one row carrying the latest
-- sighting. The consumer therefore inserts freely and never reads back to dedupe.
--
-- There is deliberately no first_seen column. It was written and removed after measuring: TTL is applied
-- per part, so an expired part is dropped before it can contribute its minimum to a merge, and a
-- first_seen would silently mean "first seen inside the retention window" while reading as "first seen".
-- A column whose name overstates what it holds is worse than an absent one, and nothing needs it.
--
-- READ IT WITH GROUP BY, NOT FINAL. Merges are eventual, so a plain SELECT can return several unmerged
-- rows for one sort key. Grouping by that key and taking max(last_seen) is exact whatever the merge
-- state is, which is what the aggregate column type is for. FINAL would be slower and buy nothing.
-- Verified against ClickHouse 24.8: the grouped read returned the correct latest sighting across three
-- unmerged parts, and OPTIMIZE FINAL then converged to the same answer.
--
-- Retention is column-driven and per tier, exactly as condux.events has been since 0002. The inventory
-- must not outlive the events it describes, since that is the retention the plan promises. Because
-- last_seen refreshes while a release keeps reporting, a deployed release keeps its inventory and a
-- retired one expires on its own, with no cron and no reaper.
--
-- Idempotent, applied in filename order after 0004_user_key.sql.

CREATE TABLE IF NOT EXISTS condux.release_modules
(
    project_id     String                  CODEC(ZSTD(1)),
    release        LowCardinality(String)  CODEC(ZSTD(1)),
    environment    LowCardinality(String)  CODEC(ZSTD(1)),
    -- Derived from the event's platform, never from the package name. Empty when the platform does not
    -- map to a known ecosystem, because a guessed ecosystem is worse than an absent one: it reads as a
    -- confident answer to a question we did not actually resolve. See ADR-0041 on why a wrong ecosystem
    -- string is the specific failure this feature has to avoid.
    ecosystem      LowCardinality(String)  CODEC(ZSTD(1)),
    -- Stored spelled as the reporting runtime spells it, so it displays as the developer wrote it, but
    -- MATCHED case insensitively against a CveFinding: a scanner reports a registry's spelling and an
    -- SDK reports the runtime's, and PyPI and NuGet both treat names case insensitively, so the two
    -- legitimately disagree. A missed match would read as "not observed", which is the one direction
    -- this feature must not get wrong.
    package        String                  CODEC(ZSTD(1)),
    version        String                  CODEC(ZSTD(1)),
    last_seen      SimpleAggregateFunction(max, DateTime),
    -- Mirrors Condux.Core.Plans.PlanCatalog.DefaultRetentionDays; SQL cannot reference the constant.
    -- The consumer stamps the real per-tier value from the retention-days Kafka header it already reads.
    retention_days UInt16 DEFAULT 90
)
ENGINE = AggregatingMergeTree
ORDER BY (project_id, release, environment, ecosystem, package, version)
TTL last_seen + toIntervalDay(retention_days)
SETTINGS index_granularity = 8192;
