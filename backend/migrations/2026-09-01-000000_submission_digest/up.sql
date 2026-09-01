-- A fingerprint of the whole submission, so a different run reusing an id cannot
-- pass as a retry. IF NOT EXISTS so this is a no-op on a database created from
-- the current init script.
ALTER TABLE runs ADD COLUMN IF NOT EXISTS submission_digest TEXT NOT NULL DEFAULT '';
