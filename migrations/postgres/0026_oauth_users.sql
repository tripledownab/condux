-- Federated sign-in accounts (#71: "sign in with Google" / OIDC) have no local password, so
-- password_hash becomes nullable. A NULL password_hash means the account can only authenticate through
-- its identity provider; the /api/auth/login path rejects a null hash. Existing email+password accounts
-- are unaffected. Idempotent: DROP NOT NULL is a no-op when the column is already nullable, and applied
-- in filename order after 0025_org_notifications.sql.

ALTER TABLE users ALTER COLUMN password_hash DROP NOT NULL;
