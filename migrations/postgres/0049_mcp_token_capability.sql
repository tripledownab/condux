-- The MCP write tier, slice 1 (ADR-0046): what an MCP token is allowed to do, and who wrote a note.
--
-- capability is an ordered McpCapability: 0 read, 1 triage. The DEFAULT is the whole safety property.
-- Every token minted before this migration was issued on a documented read-only promise, and defaulting
-- to 0 means this migration cannot widen one. There is no endpoint that edits the column either: a
-- capability is chosen at mint and changed by revoking, so a token's authority is knowable from its row.
ALTER TABLE mcp_tokens ADD COLUMN IF NOT EXISTS capability SMALLINT NOT NULL DEFAULT 0;

-- The enum is ORDERED and the gate is `capability >= required`, so a value above the highest one we know
-- about would out-rank every tool and grant all of them. Nothing writes such a value today (the mint
-- endpoint parses a name), but the column is where that has to be impossible rather than merely unused.
-- Adding a capability therefore means widening this list, which is the correct amount of friction for a
-- change that grants an existing credential shape more authority.
-- Idempotent the way 0010 already does it: drop by name, then add. A pg_constraint name lookup would
-- match the name in any relation, so this is both simpler and more exact about what it is guarding.
ALTER TABLE mcp_tokens DROP CONSTRAINT IF EXISTS mcp_tokens_known_capability;
ALTER TABLE mcp_tokens
    ADD CONSTRAINT mcp_tokens_known_capability CHECK (capability IN (0, 1));

-- A note written over MCP has no user to attribute it to (author_user_id is a users FK), so the token
-- itself is the author. SET NULL on delete matches author_user_id: the note text is history and survives
-- whatever wrote it. Revoking a token does not delete its row, so a revoked token still names its notes.
ALTER TABLE issue_notes ADD COLUMN IF NOT EXISTS author_mcp_token_id UUID
    REFERENCES mcp_tokens (id) ON DELETE SET NULL;

-- At most one author, never two. Not "exactly one": author_user_id is already ON DELETE SET NULL, so a
-- note whose author deleted their account legitimately has neither, and a stricter check would reject a
-- state the table has always allowed.
ALTER TABLE issue_notes DROP CONSTRAINT IF EXISTS issue_notes_single_author;
ALTER TABLE issue_notes
    ADD CONSTRAINT issue_notes_single_author
    CHECK (author_user_id IS NULL OR author_mcp_token_id IS NULL);
