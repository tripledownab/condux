-- Supply-chain CVE fixes (#117 slice 2): a Conductor run that opens a draft PR bumping a vulnerable
-- dependency to its first patched version. A separate domain from the issue-keyed fix_suggestions —
-- a CVE bump is keyed on the linked repo + the advisory (GHSA), not on an error/issue, and does NOT
-- take part in the issue-occurrence verification loop (ADR-0019). It reuses the same Conductor
-- provider/gateway + the same per-org AI-fix allowance and cost model. Idempotent.

CREATE TABLE IF NOT EXISTS cve_fix_runs (
    id            UUID        PRIMARY KEY,
    repo_link_id  UUID        NOT NULL REFERENCES repo_links (id) ON DELETE CASCADE,
    ghsa_id       TEXT        NOT NULL,             -- the GitHub advisory id (stable key for the CVE)
    cve_id        TEXT,                             -- the CVE id when one is assigned (advisory may predate it)
    package       TEXT        NOT NULL,
    ecosystem     TEXT        NOT NULL,             -- npm / pip / maven / ... (drives the manifest candidates)
    from_range    TEXT        NOT NULL,             -- the vulnerable version range
    to_version    TEXT        NOT NULL,             -- the first patched version the bump targets
    advisory_url  TEXT        NOT NULL DEFAULT '',
    status        SMALLINT    NOT NULL DEFAULT 1,   -- FixStatus: 1=pending 2=running 3=succeeded 4=failed 5=cancelled
    provider      TEXT        NOT NULL DEFAULT '',
    model         TEXT        NOT NULL DEFAULT '',
    branch        TEXT        NOT NULL DEFAULT '',
    pr_url        TEXT        NOT NULL DEFAULT '',
    summary       TEXT        NOT NULL DEFAULT '',
    input_tokens  BIGINT      NOT NULL DEFAULT 0,
    output_tokens BIGINT      NOT NULL DEFAULT 0,
    actor         TEXT        NOT NULL DEFAULT '',
    created_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at    TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS cve_fix_runs_repo_idx ON cve_fix_runs (repo_link_id, created_at DESC);

-- Guard against a duplicate PR / burnt allowance from a double-click: at most one in-flight run per
-- (repo, advisory). A concluded run (succeeded/failed) leaves the slot free to re-run.
CREATE UNIQUE INDEX IF NOT EXISTS cve_fix_runs_active_uq
    ON cve_fix_runs (repo_link_id, ghsa_id) WHERE status IN (1, 2);
