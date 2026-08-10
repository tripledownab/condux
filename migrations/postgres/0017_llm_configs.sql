-- BYO-key registry (#65): a per-org LLM provider config so enterprises run the Conductor on their own
-- API key. The key is stored encrypted (AES-256-GCM, sealed blob = nonce||tag||ciphertext) — the
-- plaintext never touches the database. One active config per org for v1.
CREATE TABLE IF NOT EXISTS llm_configs (
    org_id        BIGINT      PRIMARY KEY REFERENCES orgs (id) ON DELETE CASCADE,
    provider      TEXT        NOT NULL,
    model         TEXT        NOT NULL,
    base_url      TEXT        NOT NULL DEFAULT '',
    key_encrypted BYTEA       NOT NULL,
    created_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at    TIMESTAMPTZ NOT NULL DEFAULT now()
);
