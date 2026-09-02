DROP INDEX IF EXISTS run_players_identity_idx;

ALTER TABLE run_players
    DROP COLUMN IF EXISTS identity,
    DROP COLUMN IF EXISTS display_name,
    DROP COLUMN IF EXISTS player_id;
