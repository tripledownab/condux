-- Enterprise SSO (#72, ADR-0032): a per-org OIDC IdP config so an org's users sign in through their own
-- identity provider (Okta, Entra, Auth0, Keycloak, ...). Mirrors the BYO-key registry (0017): plaintext
-- config columns + the client secret sealed with AES-256-GCM (SecretBox blob = nonce||tag||ciphertext) so
-- the plaintext never touches the database. One IdP per org. email_domain is the login routing key
-- (you@acme.com -> Acme's config -> the IdP), so it is globally unique.
CREATE TABLE IF NOT EXISTS sso_configs (
    org_id                  BIGINT      PRIMARY KEY REFERENCES orgs (id) ON DELETE CASCADE,
    email_domain            TEXT        NOT NULL UNIQUE,
    issuer                  TEXT        NOT NULL,
    authorization_endpoint  TEXT        NOT NULL,
    token_endpoint          TEXT        NOT NULL,
    client_id               TEXT        NOT NULL,
    client_secret_encrypted BYTEA       NOT NULL,
    created_at              TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at              TIMESTAMPTZ NOT NULL DEFAULT now()
);
