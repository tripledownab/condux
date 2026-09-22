-- Salt the pseudonym behind "users affected", per project.
--
-- Event.UserKey is a hash of the strongest identifier an SDK sent: the user id, else the email, else the
-- username, else the IP. It is derived at the relay BEFORE the scrubber redacts the email and drops the
-- IP, so the raw values never reach storage while the same person still counts once across events.
--
-- It was an unsalted, unkeyed SHA-256, which does not survive the identifiers it is built from. An email
-- comes from a list. A username comes from a smaller one. An IPv4 address comes from a space of 2^32,
-- which can simply be enumerated, so hashing an address and storing that is not the same as dropping
-- it. Anyone holding the stored value could recover the input, and the stored value is not only in the
-- database: it is serialised into the event payload blob and read by the dashboard, so any member of the
-- project has it without touching a database at all.
--
-- A per-project salt rather than one key for the deployment. The relay already resolves and caches the
-- project row on every ingest, so this costs no extra round trip, no new environment variable and no
-- deployment wiring. It also closes what one shared key would leave open on a multi-tenant install:
-- anyone can sign up and own an org, so with a single key they could submit events carrying identifiers
-- they choose, read the derived keys back, and build a table that re-identifies any other project's
-- users. A salt nobody else holds makes that table worth nothing outside the project that built it.
--
-- EXISTING ROWS ARE NOT RE-KEYED, and cannot be. The raw identifier was never stored, which is the point
-- of the design, so there is nothing to re-derive from. Rows written before this keep their old hashes,
-- rows after it get salted ones, and a count over a window spanning the change counts one person twice.
-- Per-tier retention expires the old rows within 30 days on Free and 90 on paid, so it resolves itself.
--
-- REPLAY-SAFE WITHOUT A GUARD, unlike the backfill in 0052, and for a reason worth stating because the
-- two look alike. 0052 backfilled with an explicit UPDATE, which run.sh would re-run on every deploy,
-- so it needed guarding. This backfills through the column DEFAULT instead, and ADD COLUMN IF NOT
-- EXISTS skips the whole subcommand once the column is there, so a replay cannot re-salt a row. A
-- project that kept its salt keeps every user key it has already derived.
--
-- Two gen_random_uuid() calls rather than gen_random_bytes, which lives in pgcrypto. Nothing in this
-- schema enables that extension, and creating it needs rights a managed Postgres often withholds, so
-- requiring it here would fail an install for a value the core function can supply. Same idiom as 0052.
ALTER TABLE projects
    ADD COLUMN IF NOT EXISTS user_key_salt TEXT
        NOT NULL DEFAULT replace(gen_random_uuid()::text, '-', '')
                      || replace(gen_random_uuid()::text, '-', '');

-- The DEFAULT exists only to give the rows above a salt each, since Postgres evaluates a column default
-- per row. Dropping it makes the application supply one, so a project inserted later cannot silently
-- receive a salt that no code chose. Same reasoning as the verification token in 0052.
ALTER TABLE projects ALTER COLUMN user_key_salt DROP DEFAULT;
