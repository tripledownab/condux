-- Billing (Stripe): link an org to its Stripe customer + subscription so webhook events can flip the
-- org's plan tier. `tier` already exists (0002); these are the Stripe handles the webhook resolves an
-- event back to an org by. Nullable — an org has no Stripe linkage until it completes a checkout.
-- Idempotent (IF NOT EXISTS), applied in filename order after 0026_oauth_users.sql.

ALTER TABLE orgs ADD COLUMN IF NOT EXISTS stripe_customer_id     TEXT;
ALTER TABLE orgs ADD COLUMN IF NOT EXISTS stripe_subscription_id TEXT;

-- The webhook looks an org up by its Stripe customer id on every subscription event.
CREATE INDEX IF NOT EXISTS orgs_stripe_customer_idx ON orgs (stripe_customer_id);
