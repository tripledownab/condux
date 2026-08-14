-- Align runner_tokens with the other machine-token tables (mcp_tokens, release_tokens).
--
-- 0040 created it with label and last_seen_at; the shared token repository that landed alongside speaks
-- the majority shape, name and last_used_at, so every mint failed with 42703 and the first click in the
-- dashboard surfaced it. Renaming is safe everywhere: the insert is the table's only writer and it has
-- never succeeded, so no environment can hold a row.
--
-- RENAME COLUMN has no IF EXISTS, so idempotency is a guard on the current name.
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.columns
               WHERE table_name = 'runner_tokens' AND column_name = 'label') THEN
        ALTER TABLE runner_tokens RENAME COLUMN label TO name;
    END IF;

    IF EXISTS (SELECT 1 FROM information_schema.columns
               WHERE table_name = 'runner_tokens' AND column_name = 'last_seen_at') THEN
        ALTER TABLE runner_tokens RENAME COLUMN last_seen_at TO last_used_at;
    END IF;
END $$;
