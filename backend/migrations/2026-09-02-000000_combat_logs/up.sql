-- The fight itself, and what can be read off it.
--
-- Kept apart from runs deliberately. A ladder needs four numbers per player and
-- must not wait on a document to get them, and a log that never arrives -- an
-- upload that failed, a server that died mid-fight -- must leave the run intact
-- rather than absent.

CREATE TABLE run_logs (
    run_id     TEXT PRIMARY KEY REFERENCES runs(id) ON DELETE CASCADE,
    format     INTEGER NOT NULL,

    -- Stored exactly as it arrived, still gzipped. Serving it back is then a
    -- copy rather than a re-encode, and a replay gets the bytes the server
    -- wrote rather than a round trip through this schema's opinions.
    body       BYTEA NOT NULL,
    byte_size  BIGINT NOT NULL,

    events     INTEGER NOT NULL,
    truncated  BOOLEAN NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Derived once, at upload, rather than by parsing a blob on every page view.
CREATE TABLE run_player_stats (
    run_id           TEXT NOT NULL REFERENCES runs(id) ON DELETE CASCADE,
    combat_id        BIGINT NOT NULL,
    player_id        TEXT NOT NULL,
    display_name     TEXT NOT NULL,
    class_name       TEXT NOT NULL,

    damage_done      BIGINT NOT NULL,
    healing_done     BIGINT NOT NULL,

    -- The difference between healing done and healing that mattered. Without it
    -- an HPS column rewards topping up somebody who is already full.
    overhealing      BIGINT NOT NULL,

    damage_taken     BIGINT NOT NULL,
    damage_absorbed  BIGINT NOT NULL,

    -- Damage taken from an ability this player was judged to be standing inside.
    -- The server records where everyone was relative to each telegraph's edge,
    -- so this is measured rather than inferred from "was hurt while something
    -- was happening".
    avoidable_damage BIGINT NOT NULL,

    interrupts       INTEGER NOT NULL,
    dispels          INTEGER NOT NULL,
    deaths           INTEGER NOT NULL,
    alive_ms         BIGINT NOT NULL,

    PRIMARY KEY (run_id, combat_id)
);

-- The breakdown behind each of those totals.
CREATE TABLE run_ability_stats (
    run_id    TEXT NOT NULL REFERENCES runs(id) ON DELETE CASCADE,
    combat_id BIGINT NOT NULL,
    ability   TEXT NOT NULL,
    damage    BIGINT NOT NULL,
    healing   BIGINT NOT NULL,
    hits      INTEGER NOT NULL,
    casts     INTEGER NOT NULL,
    PRIMARY KEY (run_id, combat_id, ability)
);

CREATE INDEX run_player_stats_player_idx ON run_player_stats (player_id);
