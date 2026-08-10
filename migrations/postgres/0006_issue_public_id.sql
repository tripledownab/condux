-- Public, non-enumerable issue identifier for URLs and the API. The BIGSERIAL id stays INTERNAL
-- (ClickHouse events + the rollup MV + the consumer all key on it); only public_id is ever exposed,
-- so /issues/<uuid> no longer leaks volume or ordering. New issues get a UUIDv7 from the app
-- (time-ordered, so this unique index stays healthy); existing rows are backfilled with
-- gen_random_uuid() (v4). Both are opaque and non-sequential. Idempotent.
ALTER TABLE issues ADD COLUMN IF NOT EXISTS public_id UUID NOT NULL DEFAULT gen_random_uuid();

CREATE UNIQUE INDEX IF NOT EXISTS issues_public_id_idx ON issues (public_id);
