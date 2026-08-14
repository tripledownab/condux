-- Customer-hosted runners (ADR-0033 slice 4, #68).
--
-- A runner is a process the customer deploys. It leases work over an authenticated outbound long poll
-- instead of reading Kafka, executes it on their compute with their own credentials, and reports back.
-- Condux keeps orchestration, metering and the audit trail; the customer keeps the code and the keys.

-- The credential a runner presents. Same shape as release and MCP tokens: prefixed secret shown once,
-- only the SHA-256 stored, revocable. Scoped to an org rather than a project, because a runner serves
-- whatever work that org produces.
CREATE TABLE IF NOT EXISTS runner_tokens (
    id          UUID        PRIMARY KEY,
    org_id      BIGINT      NOT NULL REFERENCES orgs (id) ON DELETE CASCADE,
    label       TEXT        NOT NULL DEFAULT '',
    token_hash  TEXT        NOT NULL UNIQUE,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    last_seen_at TIMESTAMPTZ,
    revoked_at  TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS runner_tokens_org_idx ON runner_tokens (org_id, created_at DESC);

-- The lease itself lives on the run, not in a separate queue, so there is one row per fix and no way for
-- a queue entry and its run to disagree about state.
--
-- leased_by is a lease id the server generates when it hands the job out, and the runner must present it
-- back to heartbeat or report. It cannot be a name the runner chooses: every runner in an org shares one
-- token, so a self-reported identity would let any of them report on another's job. lease_expires_at is
-- what makes the work reclaimable: a customer's machine can die without telling us, and without an expiry
-- the fix would sit Running forever.
ALTER TABLE fix_suggestions
    ADD COLUMN IF NOT EXISTS leased_by         TEXT,
    ADD COLUMN IF NOT EXISTS leased_at         TIMESTAMPTZ,
    ADD COLUMN IF NOT EXISTS lease_expires_at  TIMESTAMPTZ;

-- The claim reads pending work whose lease has lapsed, newest last. Partial, because a claimable job is a
-- vanishing fraction of the table and the index should not carry every finished run.
CREATE INDEX IF NOT EXISTS fix_suggestions_claimable_idx
    ON fix_suggestions (created_at)
    WHERE status IN (1, 2);
