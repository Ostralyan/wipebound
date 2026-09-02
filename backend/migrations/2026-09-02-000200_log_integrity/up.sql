-- A log is evidence, so it stops being rewritable.
--
-- Uploading one used to delete whatever was there and replace it, along with
-- every statistic derived from it. Any authenticated server could therefore
-- rewrite the meters and the player history behind a run it did not play.
-- With a digest stored, an identical retry is a retry and different bytes are a
-- conflict -- the same rule runs already follow.
ALTER TABLE run_logs ADD COLUMN log_digest TEXT NOT NULL DEFAULT '';
