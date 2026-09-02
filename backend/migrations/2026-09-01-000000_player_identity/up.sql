-- Give a run a player it can name.
--
-- run_players identified its subject by ENet peer id: a random integer minted
-- fresh on every connection, so two runs by one person shared no key and the
-- ladder could rank without ever attributing. These columns are what it can
-- attribute to.
--
-- A SEPARATE MIGRATION rather than an edit to the initial one. Diesel records
-- what it has applied and will not run it twice, and the docker-compose volume
-- only initialises on first creation -- so editing the first migration in place
-- leaves every existing database without these columns and every submission
-- failing on them. It is only ever correct to add.

ALTER TABLE run_players
    ADD COLUMN player_id    TEXT,
    ADD COLUMN display_name TEXT,
    ADD COLUMN identity     TEXT;

-- Rows written before identity existed are exactly what "anonymous" describes:
-- nobody checked who they belonged to. Derived from the run and the peer so the
-- value is stable, unique, and obviously synthetic rather than pretending to be
-- somebody's real id.
UPDATE run_players
   SET player_id    = 'legacy-' || md5(run_id || '-' || peer::text),
       display_name = 'Unknown',
       identity     = 'anonymous'
 WHERE player_id IS NULL;

ALTER TABLE run_players
    ALTER COLUMN player_id    SET NOT NULL,
    ALTER COLUMN display_name SET NOT NULL,
    ALTER COLUMN identity     SET NOT NULL;

-- "Every run this player was in", which is the question the peer id could never
-- answer.
CREATE INDEX run_players_identity_idx ON run_players (identity, player_id);
