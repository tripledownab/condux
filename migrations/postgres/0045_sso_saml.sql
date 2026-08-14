-- SAML SSO (#23, ADR-0032 slice 2): sso_configs learns a second protocol. protocol 0 = OIDC (the
-- existing rows), 1 = SAML. A SAML config stores the IdP's Single Sign-On URL (redirect binding) and its
-- public signing certificate (PEM) instead of the OIDC endpoints + client credentials, so those columns
-- become nullable; issuer stays required and holds the IdP entity ID for SAML. The certificate is public
-- key material, not a secret, so it is stored plaintext (no SecretBox).
ALTER TABLE sso_configs ADD COLUMN IF NOT EXISTS protocol SMALLINT NOT NULL DEFAULT 0;
ALTER TABLE sso_configs ADD COLUMN IF NOT EXISTS saml_sso_url TEXT;
ALTER TABLE sso_configs ADD COLUMN IF NOT EXISTS saml_certificate TEXT;
ALTER TABLE sso_configs ALTER COLUMN authorization_endpoint DROP NOT NULL;
ALTER TABLE sso_configs ALTER COLUMN token_endpoint DROP NOT NULL;
ALTER TABLE sso_configs ALTER COLUMN client_id DROP NOT NULL;
ALTER TABLE sso_configs ALTER COLUMN client_secret_encrypted DROP NOT NULL;
