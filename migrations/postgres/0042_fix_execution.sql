-- Where an org's fix runs execute (ADR-0033 slice 4c, #68).
--
-- 0 = hosted: the request goes on the Kafka topic and our Conductor runs it, which is what every org has
-- done until now and stays the default. 1 = runner: the request becomes a leasable row instead, and the
-- customer's own process takes it, runs it on their compute with their credentials and reports back.
--
-- Per org rather than per project, because the runner token and the lease are org scoped: a runner serves
-- whatever work its org produces.
ALTER TABLE orgs
    ADD COLUMN IF NOT EXISTS fix_execution SMALLINT NOT NULL DEFAULT 0;
