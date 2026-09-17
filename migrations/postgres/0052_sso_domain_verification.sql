-- Prove control of an email domain before its SSO claim routes a login (ADR-0043).
--
-- email_domain was globally UNIQUE and first-come-first-served, and nothing checked that the claiming org
-- owned the domain. ADR-0042 stopped a false claim taking an existing account. Two things survived it: an
-- org could hold a domain it does not own and block the real owner from ever configuring SSO, and it could
-- have its provider assert an unheld address in that domain, which JIT provisioning then created.
--
-- The org proves control by publishing a TXT record carrying verification_token. Until then the claim is
-- provisional: it is saved, and it does not route.
--
-- Exclusivity moves from insertion to verification, which is the whole point. A plain UNIQUE key is taken
-- on the first save, so a squatter would keep the domain even after verification shipped and the real
-- owner still could not onboard. A partial index over verified rows only lets several orgs hold the same
-- provisional claim, and gives the domain to whichever one proves it.
ALTER TABLE sso_configs
    ADD COLUMN IF NOT EXISTS verification_token   TEXT
        NOT NULL DEFAULT replace(gen_random_uuid()::text, '-', ''),
    ADD COLUMN IF NOT EXISTS verification_lost_at TIMESTAMPTZ;

-- The DEFAULT exists only to give the rows above a token each (Postgres evaluates a column default per
-- row). Dropping it makes the application supply one, so an insert that forgot cannot silently receive a
-- token nobody was ever shown.
ALTER TABLE sso_configs ALTER COLUMN verification_token DROP DEFAULT;

-- verified_at is added and backfilled together, guarded on its own absence, so the grandfathering runs
-- exactly once in the life of a database. deploy/migrations/run.sh replays EVERY file on every upgrade,
-- so a plain `UPDATE ... WHERE verified_at IS NULL` would hand verification to every provisional claim
-- in the database on the next deploy, a squatter's included, and undo the whole of this migration.
-- Postgres runs DDL transactionally, so the column and the backfill land together or not at all.
--
-- Grandfathering itself is ADR-0043's decision: a self-hoster with working SSO would otherwise have it
-- stop on deploy, and breaking a working install to enforce a check that install may not need is worse
-- than the risk it removes.
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = current_schema()
          AND table_name = 'sso_configs'
          AND column_name = 'verified_at')
    THEN
        ALTER TABLE sso_configs ADD COLUMN verified_at TIMESTAMPTZ;
        UPDATE sso_configs SET verified_at = now();
    END IF;
END $$;

ALTER TABLE sso_configs DROP CONSTRAINT IF EXISTS sso_configs_email_domain_key;

CREATE UNIQUE INDEX IF NOT EXISTS sso_configs_verified_domain_idx
    ON sso_configs (email_domain) WHERE verified_at IS NOT NULL;
