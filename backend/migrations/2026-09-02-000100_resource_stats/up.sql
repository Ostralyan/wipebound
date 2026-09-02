-- What a rotation cost, not only what it produced.
--
-- A separate migration rather than an edit to the one beside it: that one has
-- been applied, and an applied migration is only ever added to.

ALTER TABLE run_player_stats  ADD COLUMN resource_spent BIGINT NOT NULL DEFAULT 0;
ALTER TABLE run_ability_stats ADD COLUMN resource_spent BIGINT NOT NULL DEFAULT 0;
