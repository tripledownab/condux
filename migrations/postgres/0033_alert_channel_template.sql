-- Per-channel custom alert message template. A channel may override the built-in default message with plain
-- text using {{token}} placeholders (title/culprit/level/event/project/issue); NULL = use the default, which
-- is what "restore default" clears back to. Idempotent.

ALTER TABLE alert_channels ADD COLUMN IF NOT EXISTS template TEXT;
