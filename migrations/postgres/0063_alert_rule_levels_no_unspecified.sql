-- Level 0 (Unspecified) leaves alert rules and issues. No ingest path stores an event at it any longer: a
-- Sentry event with no level is an error, and so is an OTLP record carrying an exception with no
-- severity, the only OTLP record at level 0 that was ever stored. Both statements are idempotent, since a
-- second run finds no 0 left.

-- Issues stored at 0 before that change keep their level until they recur, and resolving or assigning one
-- fires its alert at the stored level. They become Error, the level they would carry if they arrived now,
-- so those alerts match the rules rewritten below.
UPDATE issues SET level = 4 WHERE level = 0;

-- Each 0 in a rule becomes 4 (Error); duplicates are removed and the levels are sorted. Rewriting rather
-- than dropping keeps a rule that listed only 0 from ending up with no levels, and keeps the dashboard able
-- to save an edit of it, since the API now refuses 0.
UPDATE alert_rules
SET levels = ARRAY(SELECT DISTINCT unnest(array_replace(levels, 0::smallint, 4::smallint)) ORDER BY 1)::smallint[]
WHERE 0 = ANY(levels);
